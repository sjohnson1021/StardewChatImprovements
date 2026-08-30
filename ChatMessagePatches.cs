using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

/// <summary>
/// Harmony patches for rendering chat messages with enhanced features:
/// - Clickable URLs
/// - Bold sender names
/// - Properly aligned underlines
/// </summary>
internal class ChatMessagePatches
{
    // Constants
    // The drawable width of a message: the chat box less the 12px inset it is drawn at and the
    // matching gap on the right. Height measurement and drawing must use the same number, or a
    // message reserves a different number of lines than it paints.
    private const float MaxLineWidth = 872f;
    private const float EmojiScale = 4f;
    private const int EmojiSize = 9;
    private const int UnderlineYOffset = 8;

    /// <summary>Marker Item Chat Link wraps around the data behind a linked item.</summary>
    private const string ItemLinkMarker = "{icl:v1|";
    
    private static readonly Color UrlColor = new(100, 149, 237);
    private static readonly Color ShadowColorBase = new(125, 125, 125, 255);

    // State
    private static readonly List<UrlRegion> ActiveUrlRegions = new();
    private static bool _wasMousePressed;

    // Cache
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ConditionalWeakTable<ChatMessage, List<RichTextToken>> CachedMessageTokens = new();
    private static readonly ConditionalWeakTable<ChatMessage, string> SenderNames = new();

    #region Nested Types

    private sealed class RichTextToken
    {
        public string Text = "";
        public bool IsBold;
        public bool IsUrl;
        public bool IsUnderlined;
        public bool IsEmoji;
        public int EmojiIndex;
        public bool IsNewLine;
        public float Width;
    }

    private sealed class UrlRegion
    {
        public Rectangle Bounds;
        public ChatMessage? Message;
        public string Url = "";
    }

    #endregion

    #region Helpers

    /// <summary>Acts on a clicked link according to <see cref="ModConfig.LinkClickBehavior" />.</summary>
    private static void ActivateLink(string url)
    {
        ITranslationHelper? t = ModEntry.Instance?.Helper.Translation;

        if ((ModEntry.Instance?.Config.LinkClickBehavior ?? LinkClickAction.Copy) == LinkClickAction.Open)
        {
            OpenUrl(url);
            Game1.playSound("drumkit6");
            return;
        }

        ClipboardHelper.SetText(url);
        Game1.playSound("drumkit6");
        Game1.addHUDMessage(new HUDMessage(t?.Get("hud.linkCopied") ?? "Link copied to clipboard",
            HUDMessage.newQuest_type));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.Instance?.Monitor.Log($"Refused to open invalid URL: {url}", LogLevel.Warn);
                return;
            }

            ModEntry.Instance?.Monitor.Log($"Opening URL: {url}", LogLevel.Debug);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Failed to open URL: {ex.Message}", LogLevel.Error);
            var t = ModEntry.Instance?.Helper.Translation;
            Game1.addHUDMessage(new HUDMessage(t?.Get("hud.failedToOpenUrl") ?? "Failed to open URL",
                HUDMessage.error_type));
        }
    }

    #endregion

    #region Sender Name Tracking

    [HarmonyPatch(typeof(ChatBox), "receiveChatMessage")]
    public sealed class ReceiveChatMessagePatch
    {
        private static void Postfix(ChatBox __instance, long sourceFarmer, int chatKind, string message)
        {
            if (__instance.messages.Count == 0) return;
            ChatMessage lastMsg = __instance.messages[^1];

            string? senderName = null;
            if (chatKind == 0 || chatKind == 3) // Chat or Private
            {
                Farmer? farmer = null;
                if (sourceFarmer == Game1.player.UniqueMultiplayerID)
                    farmer = Game1.player;
                else if (Game1.otherFarmers.TryGetValue(sourceFarmer, out Farmer? other))
                    farmer = other;

                if (farmer != null)
                {
                    senderName = ChatBox.formattedUserName(farmer) + ": ";
                }
            }

            if (!string.IsNullOrEmpty(senderName))
            {
                SenderNames.Add(lastMsg, senderName);
            }

            // Only now can the height be worked out: the sender name decides how the message is
            // tokenised, and the tokens decide where it wraps.
            ChatBoxScrollPatches.FixMessageHeight(lastMsg, __instance.chatBox.Width);
        }
    }

    #endregion

    #region Rendering

    [HarmonyPatch(typeof(ChatMessage), "draw")]
    public sealed class DrawMessagePatch
    {
        // Runs last, so a mod that only wants to watch a message being drawn still gets to.
        // This prefix skips the original, and Harmony stops running prefixes at the first one
        // that does, which would otherwise silence whoever happened to be sorted behind it.
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(ChatMessage __instance, SpriteBatch b, int x, int y)
        {
            // Item Chat Link draws messages holding an item link from its own prefix, so it can
            // place the hover regions that make the item inspectable. Only one prefix can take
            // over a method, and with neither declaring a priority the winner came down to patch
            // registration order -- its tooltips worked or did not depending on the install.
            // Standing aside here costs those messages this mod's link colouring and bold sender
            // name, and makes the other mod's feature work everywhere rather than by luck.
            if (ModEntry.ItemChatLinkLoaded && HasItemLink(__instance))
                return true;

            SenderNames.TryGetValue(__instance, out string? senderName);

            // Every message this mod is allowed to draw, it draws. Vanilla wraps a message at
            // whole-snippet granularity, which cannot break a long line at all now that the
            // wrapping pass in receiveChatMessage is suppressed -- handing anything back to it
            // would send that message straight off the right edge of the chat box.
            SpriteFont? messageFont = ChatBox.messageFont(__instance.language);
            if (messageFont is null) return true;

            List<RichTextToken> tokens = CachedMessageTokens.GetValue(__instance,
                _ => ParseMessage(__instance, senderName, messageFont));

            // Clear old regions for this message
            ActiveUrlRegions.RemoveAll(r => r.Message == __instance);

            LayoutTokens(tokens, messageFont, (token, tokenX, tokenY) =>
            {
                Vector2 position = new(x + tokenX, y + tokenY);

                if (token.IsUrl)
                    DrawUrl(b, messageFont, token, position, __instance);
                else if (token.IsBold)
                    DrawBoldText(b, messageFont, token, position, __instance);
                else if (token.IsEmoji)
                    DrawEmoji(b, token, position, __instance);
                else
                    b.DrawString(messageFont, token.Text, position, __instance.color * __instance.alpha, 0f,
                        Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
            });

            return false;
        }

        /// <summary>Whether the message carries an Item Chat Link item marker.</summary>
        private static bool HasItemLink(ChatMessage message)
        {
            List<ChatSnippet> snippets = message.message;
            for (int i = 0; i < snippets.Count; i++)
            {
                if (snippets[i].message?.Contains(ItemLinkMarker, StringComparison.Ordinal) == true)
                    return true;
            }

            return false;
        }

        private static void DrawUrl(SpriteBatch b, SpriteFont font, RichTextToken token, Vector2 position, ChatMessage msg)
        {
            Color urlColor = UrlColor * msg.alpha;
            b.DrawString(font, token.Text, position, urlColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);

            // Underline
            float textHeight = font.MeasureString(token.Text).Y;
            b.Draw(Game1.staminaRect,
                new Rectangle((int)position.X + 2, (int)(position.Y + textHeight - UnderlineYOffset), (int)token.Width - 4, 2),
                urlColor);

            // A message keeps being drawn after it has faded out, so only register a click
            // target while it is actually visible. Otherwise invisible links stay clickable and
            // a click aimed at the farm opens a browser.
            if (msg.alpha <= 0.01f)
                return;

            var region = new UrlRegion
            {
                Url = token.Text,
                Bounds = new Rectangle((int)position.X, (int)position.Y, (int)token.Width, (int)textHeight),
                Message = msg
            };
            if (!ActiveUrlRegions.Any(r => r.Message == region.Message && r.Url == region.Url && r.Bounds == region.Bounds))
                ActiveUrlRegions.Add(region);
        }

        private static void DrawBoldText(SpriteBatch b, SpriteFont font, RichTextToken token, Vector2 position, ChatMessage msg)
        {
            Color shadowColor = Utility.MultiplyColor(msg.color * msg.alpha, ShadowColorBase);

            Utility.drawTextWithColoredShadow(b, token.Text, font, position,
                msg.color * msg.alpha, shadowColor, 1f, .99f, -1, 0, 2);

            if (token.IsUnderlined)
            {
                float lineHeight = font.MeasureString("(").Y;
                int lineY = (int)(position.Y + lineHeight - UnderlineYOffset);

                // Shadow Line
                b.Draw(Game1.staminaRect, new Rectangle((int)position.X, lineY, (int)token.Width - 2, 2), shadowColor);
                // Main Line
                b.Draw(Game1.staminaRect, new Rectangle((int)position.X + 2, lineY, (int)token.Width - 4, 2), msg.color * msg.alpha);
            }
        }

        private static void DrawEmoji(SpriteBatch b, RichTextToken token, Vector2 position, ChatMessage msg)
        {
            b.Draw(ChatBox.emojiTexture,
                new Vector2(position.X + 1f, position.Y - 4f),
                new Rectangle(token.EmojiIndex * EmojiSize % ChatBox.emojiTexture.Width,
                    token.EmojiIndex * EmojiSize / ChatBox.emojiTexture.Width * EmojiSize, EmojiSize, EmojiSize),
                Color.White * msg.alpha, 0f, Vector2.Zero, EmojiScale, SpriteEffects.None, 0.99f);
        }

        /// <summary>
        ///     Places every token, wrapping between tokens, and reports how many lines it took.
        /// </summary>
        /// <remarks>
        ///     Drawing and height measurement both go through here. They used to be separate
        ///     passes over different data at different widths -- the height from whole snippets at
        ///     872px, the drawing from styled fragments at 888px -- so any message that actually
        ///     wrapped reserved the wrong number of lines and ran over its neighbours.
        /// </remarks>
        private static int LayoutTokens(List<RichTextToken> tokens, SpriteFont font,
            Action<RichTextToken, float, float>? place)
        {
            float lineHeight = font.MeasureString("(").Y;
            float xPos = 0f;
            float yPos = 0f;
            int lines = 1;

            foreach (RichTextToken token in tokens)
            {
                if (token.IsNewLine)
                {
                    xPos = 0f;
                    yPos += lineHeight;
                    lines++;
                    continue;
                }

                // A token already alone on its line is never wrapped: a single word wider than
                // the chat box would otherwise push itself down forever.
                if (xPos > 0f && xPos + token.Width > MaxLineWidth)
                {
                    xPos = 0f;
                    yPos += lineHeight;
                    lines++;
                }

                place?.Invoke(token, xPos, yPos);
                xPos += token.Width;
            }

            return lines;
        }

        /// <summary>
        ///     How many lines this message occupies once laid out, or null when this mod is not
        ///     the one drawing it.
        /// </summary>
        internal static int? TryGetLineCount(ChatMessage message)
        {
            // Same reasoning as the re-wrap: decide from the text, not from the mod registry.
            if (HasItemLink(message))
                return null;

            SpriteFont? font = ChatBox.messageFont(message.language);
            if (font is null)
                return null;

            SenderNames.TryGetValue(message, out string? senderName);
            List<RichTextToken> tokens =
                CachedMessageTokens.GetValue(message, _ => ParseMessage(message, senderName, font));

            return LayoutTokens(tokens, font, null);
        }

        private static List<RichTextToken> ParseMessage(ChatMessage instance, string? senderName, SpriteFont font)
        {
            List<RichTextToken> tokens = new();

            // Find the sender name instead of assuming the message opens with it. Chat Time
            // prepends a timestamp snippet, and counting characters from the start then styled
            // the timestamp and cut the name in two -- "B" at the end of one line, "eachBum:" at
            // the start of the next.
            string plain = string.Concat(instance.message
                .Where(snippet => snippet.message != null)
                .Select(snippet => snippet.message));
            int nameStart = string.IsNullOrEmpty(senderName)
                ? -1
                : plain.IndexOf(senderName, StringComparison.Ordinal);
            int nameEnd = nameStart < 0 ? -1 : nameStart + senderName!.Length;
            // The underline stops short of the ": " separator.
            int underlineEnd = nameEnd < 0 ? -1 : Math.Max(nameStart, nameEnd - 2);

            int index = 0;
            foreach (ChatSnippet snippet in instance.message)
            {
                if (snippet.emojiIndex != -1)
                {
                    tokens.Add(new RichTextToken { IsEmoji = true, EmojiIndex = snippet.emojiIndex, Width = 40f });
                    continue;
                }

                if (snippet.message == null)
                    continue;

                if (snippet.message.Equals(Environment.NewLine, StringComparison.Ordinal))
                    tokens.Add(new RichTextToken { IsNewLine = true });
                else
                    AddSnippetTokens(tokens, snippet.message, font, index, nameStart, nameEnd, underlineEnd);

                index += snippet.message.Length;
            }

            return tokens;
        }

        private static void AddSnippetTokens(List<RichTextToken> tokens, string text, SpriteFont font,
            int baseIndex, int nameStart, int nameEnd, int underlineEnd)
        {
            int lastIndex = 0;

            foreach (Match match in UrlRegex.Matches(text))
            {
                if (match.Index > lastIndex)
                    AddStyledTextTokens(tokens, text.Substring(lastIndex, match.Index - lastIndex), font,
                        baseIndex + lastIndex, nameStart, nameEnd, underlineEnd);

                tokens.Add(new RichTextToken
                {
                    Text = match.Value,
                    IsUrl = true,
                    Width = font.MeasureString(match.Value).X
                });

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
                AddStyledTextTokens(tokens, text.Substring(lastIndex), font,
                    baseIndex + lastIndex, nameStart, nameEnd, underlineEnd);
        }

        /// <summary>
        ///     Emits one token per word, split further wherever the sender-name styling starts or
        ///     stops.
        /// </summary>
        /// <remarks>
        ///     Word granularity is what makes wrapping possible at all. The body of a message used
        ///     to be a single token, so a line too long to fit was moved down whole and then drawn
        ///     straight off the right edge of the chat box instead of wrapping inside it.
        /// </remarks>
        private static void AddStyledTextTokens(List<RichTextToken> tokens, string text, SpriteFont font,
            int baseIndex, int nameStart, int nameEnd, int underlineEnd)
        {
            int start = 0;
            while (start < text.Length)
            {
                bool isBold = IsInRange(baseIndex + start, nameStart, nameEnd);
                bool isUnderlined = IsInRange(baseIndex + start, nameStart, underlineEnd);

                int end = start;
                while (end < text.Length
                       && IsInRange(baseIndex + end, nameStart, nameEnd) == isBold
                       && IsInRange(baseIndex + end, nameStart, underlineEnd) == isUnderlined)
                {
                    end++;

                    // Cut after a space, so every token is a whole word the layout can move down
                    // as a unit. The trailing space rides along and is harmless at a line end.
                    if (text[end - 1] == ' ')
                        break;
                }

                string fragment = text.Substring(start, end - start);
                tokens.Add(new RichTextToken
                {
                    Text = fragment,
                    IsBold = isBold,
                    IsUnderlined = isUnderlined,
                    Width = font.MeasureString(fragment).X
                });

                start = end;
            }
        }

        private static bool IsInRange(int index, int start, int end)
        {
            return start >= 0 && index >= start && index < end;
        }
    }

    #endregion

    #region Interaction (Update & Cursor)

    [HarmonyPatch(typeof(ChatBox), "update")]
    public sealed class UpdatePatch
    {
        private static void Postfix(ChatBox __instance)
        {
            bool canClick = __instance.chatBox.Selected ||
                            (ModEntry.Instance?.Config.AllowUrlClickWhenChatClosed ?? true);

            if (!canClick) return;

            MouseState mouseState = Game1.input.GetMouseState();
            bool isMousePressed = mouseState.LeftButton == ButtonState.Pressed;

            if (isMousePressed && !_wasMousePressed)
            {
                HandleClick();
            }

            _wasMousePressed = isMousePressed;

            // Cleanup cache periodically
            if (Game1.ticks % 60 == 0)
                CleanupOldMessages(__instance);
        }

        private static void HandleClick()
        {
            // URL bounds are recorded in UI space, so hit-test in UI space too.
            // getMousePosition() already divides by uiScale; dividing again by zoomLevel
            // double-scales the point and the hit test never matches.
            Point mousePos = Game1.getMousePosition(ui_scale: true);

            foreach (UrlRegion region in ActiveUrlRegions.Where(r => r.Bounds.Contains(mousePos)))
            {
                ActivateLink(region.Url);
                break;
            }
        }

        private static void CleanupOldMessages(ChatBox chatBox)
        {
            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField?.GetValue(chatBox) is not List<ChatMessage> messages) return;

            ActiveUrlRegions.RemoveAll(region => region.Message != null && !messages.Contains(region.Message));
        }
    }

    [HarmonyPatch(typeof(ChatBox), "draw")]
    public sealed class DrawCursorPatch
    {
        private static void Postfix(ChatBox __instance)
        {
            bool canInteract = __instance.chatBox.Selected ||
                               (ModEntry.Instance?.Config.AllowUrlClickWhenChatClosed ?? true);

            if (!canInteract) return;

            Point mousePos = Game1.getMousePosition(ui_scale: true);

            if (ActiveUrlRegions.Any(r => r.Bounds.Contains(mousePos)))
            {
                Game1.mouseCursor = Game1.cursor_gamepad_pointer;
            }
        }
    }

    #endregion
}