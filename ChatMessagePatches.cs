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
    private const float MaxLineWidth = 888f; // Matches vanilla chat width
    private const float EmojiScale = 4f;
    private const int EmojiSize = 9;
    private const int UnderlineYOffset = 8;
    
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
        }
    }

    #endregion

    #region Rendering

    [HarmonyPatch(typeof(ChatMessage), "draw")]
    public sealed class DrawMessagePatch
    {
        private static bool Prefix(ChatMessage __instance, SpriteBatch b, int x, int y)
        {
            bool hasSenderName = SenderNames.TryGetValue(__instance, out string? senderName);

            // Check if we need to take over drawing
            if (!CachedMessageTokens.TryGetValue(__instance, out List<RichTextToken>? tokens))
            {
                bool hasUrls = false;
                var messageSnippets = __instance.message;
                for (int i = 0; i < messageSnippets.Count; i++)
                {
                    var snippet = messageSnippets[i];
                    if (snippet.message != null && UrlRegex.IsMatch(snippet.message))
                    {
                        hasUrls = true;
                        break;
                    }
                }

                if (!hasUrls && !hasSenderName)
                    return true; // Use vanilla drawing

                // Parse and cache with value factory to avoid race Add
                tokens = CachedMessageTokens.GetValue(__instance, _ => ParseMessage(__instance, senderName, ChatBox.messageFont(__instance.language)));
            }

            // Clear old regions for this message
            ActiveUrlRegions.RemoveAll(r => r.Message == __instance);

            SpriteFont? font = ChatBox.messageFont(__instance.language);
            if (font is null) return true;

            float xPos = 0f;
            float yPos = 0f;
            float lineHeight = font.MeasureString("(").Y;
            int currentTokenIndex = 0;
            int newlineCount = 0;
            foreach (RichTextToken token in tokens)
            {
                // Handle explicit newlines
                if (token.IsNewLine && newlineCount >= 0)
                {
                    xPos = 0f;
                    yPos += lineHeight;
                    newlineCount++;
                    currentTokenIndex++;
                    continue;
                }

                // Handle wrapping
                if (xPos + token.Width >= MaxLineWidth)
                {
                    xPos = 0f;
                    yPos += lineHeight;
                    newlineCount++;
                }

                Vector2 position = new(x + xPos, y + yPos);

                if (token.IsUrl)
                {
                    DrawUrl(b, font, token, position, __instance);
                }
                else if (token.IsBold)
                {
                    DrawBoldText(b, font, token, position, __instance);
                }
                else if (token.IsEmoji)
                {
                    DrawEmoji(b, token, position, __instance);
                }
                else
                {
                    b.DrawString(font, token.Text, position, __instance.color * __instance.alpha, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                }
                currentTokenIndex++;
                xPos += token.Width;
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

        private static List<RichTextToken> ParseMessage(ChatMessage instance, string? senderName, SpriteFont font)
        {
            List<RichTextToken> tokens = new();
            int senderNameRemaining = senderName?.Length ?? 0;
            // Name underline excludes the ": " separator (last 2 chars)
            int underlineRemaining = Math.Max(0, senderNameRemaining - 2);

            foreach (ChatSnippet snippet in instance.message)
            {
                if (snippet.emojiIndex != -1)
                {
                    tokens.Add(new RichTextToken { IsEmoji = true, EmojiIndex = snippet.emojiIndex, Width = 40f });
                }
                else if (snippet.message != null)
                {
                    if (snippet.message.Equals(Environment.NewLine, StringComparison.Ordinal))
                    {
                        tokens.Add(new RichTextToken { IsNewLine = true });
                    }
                    else
                    {
                        ProcessTextSnippet(tokens, snippet.message, font, ref senderNameRemaining, ref underlineRemaining);
                    }
                }
            }
            return tokens;
        }

        private static void ProcessTextSnippet(List<RichTextToken> tokens, string text, SpriteFont font,
            ref int senderRemaining, ref int underlineRemaining)
        {
            MatchCollection matches = UrlRegex.Matches(text);
            int lastIndex = 0;

            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    string segment = text.Substring(lastIndex, match.Index - lastIndex);
                    AddStyledTextTokens(tokens, segment, font, ref senderRemaining, ref underlineRemaining);
                }

                // URL
                tokens.Add(new RichTextToken
                {
                    Text = match.Value,
                    IsUrl = true,
                    Width = font.MeasureString(match.Value).X
                });

                // Consume counts if URL is somehow part of the name (unlikely but safe)
                if (senderRemaining > 0)
                {
                    senderRemaining -= match.Value.Length;
                    underlineRemaining -= match.Value.Length;
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                AddStyledTextTokens(tokens, text.Substring(lastIndex), font, ref senderRemaining, ref underlineRemaining);
            }
        }

        private static void AddStyledTextTokens(List<RichTextToken> tokens, string text, SpriteFont font,
            ref int boldRemaining, ref int underlineRemaining)
        {
            while (text.Length > 0)
            {
                bool isBold = boldRemaining > 0;
                bool isUnderlined = underlineRemaining > 0;

                int len = text.Length;
                if (isBold && boldRemaining < len) len = boldRemaining;
                if (isUnderlined && underlineRemaining < len) len = underlineRemaining;

                string fragment = text.Substring(0, len);
                tokens.Add(new RichTextToken
                {
                    Text = fragment,
                    IsBold = isBold,
                    IsUnderlined = isUnderlined,
                    Width = font.MeasureString(fragment).X
                });

                text = text.Substring(len);
                if (isBold) boldRemaining -= len;
                if (isUnderlined) underlineRemaining -= len;
            }
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