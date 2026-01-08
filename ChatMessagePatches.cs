using System.Diagnostics;
using System.Reflection;
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

internal class ChatUrlPatches
{
    private static readonly List<UrlRegion> ActiveUrlRegions = new();

    private static readonly Regex UrlRegex = new(@"https?://[^\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool _wasMousePressed;

    // Cache which messages have URLs to avoid re-checking every frame
    private static readonly HashSet<ChatMessage> MessagesWithUrls = new();

    #region Cross-Platform URL Opening

    private static void OpenUrl(string url)
    {
        try
        {
            // Validate URL starts with http:// or https://
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ModEntry.Instance?.Monitor.Log($"Refused to open invalid URL: {url}", LogLevel.Warn);
                return;
            }

            ModEntry.Instance?.Monitor.Log($"Opening URL: {url}", LogLevel.Debug);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
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

    private class UrlRegion
    {
        public Rectangle Bounds;
        public ChatMessage? Message;
        public string Url = "";
    }

    #region Sender Name Tracking

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChatMessage, string> SenderNames =
        new();

    [HarmonyPatch(typeof(ChatBox), "receiveChatMessage")]
    public class ReceiveChatMessagePatch
    {
        private static void Postfix(ChatBox __instance, long sourceFarmer, int chatKind, string message)
        {
            // Get the last added message
            if (__instance.messages.Count == 0) return;
            ChatMessage lastMsg = __instance.messages[^1];

            // Resolve sender name
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
                    senderName = ChatBox.formattedUserName(farmer);
                    // Add the separator that the game adds
                    senderName += ": ";
                }
            }

            if (!string.IsNullOrEmpty(senderName))
            {
                SenderNames.Add(lastMsg, senderName);
            }
        }
    }

    #endregion

    #region URL and Bold Name Rendering

    /// <summary>
    ///     Patch to render URLs with special formatting and track their bounds
    ///     Also renders sender name in bold
    /// </summary>
    [HarmonyPatch(typeof(ChatMessage), "draw")]
    public class DrawMessagePatch
    {
        private static bool Prefix(ChatMessage __instance, SpriteBatch b, int x, int y)
        {
            // Check if this message has URLs (using cache or scanning)
            bool hasUrls = MessagesWithUrls.Contains(__instance);

            // Check if we have a sender name to bold
            bool hasSenderName = SenderNames.TryGetValue(__instance, out string? senderName);

            if (!hasUrls && !hasSenderName)
            {
                // Not in cache - scan for URLs to decide if we need to take over drawing
                // We ALWAYS take over if we need to bold the name, but if we don't know yet:
                foreach (ChatSnippet? snippet in __instance.message)
                {
                    if (snippet.message == null || !UrlRegex.IsMatch(snippet.message)) continue;
                    hasUrls = true;
                    MessagesWithUrls.Add(__instance);
                    break;
                }
            }

            // If no URLs and no name to bold, use original method
            if (!hasUrls && !hasSenderName) return true;

            // Clear URL regions for this message
            ActiveUrlRegions.RemoveAll(r => r.Message == __instance);

            float xPositionSoFar = 0f;
            float yPositionSoFar = 0f;
            SpriteFont? font = ChatBox.messageFont(__instance.language);
            if (font is null) return true;

            // Track how much text we've drawn to know when we are inside the sender name
            int charactersDrawnSoFar = 0;
            int senderNameLength = senderName?.Length ?? 0;

            for (int i = 0; i < __instance.message.Count; i++)
            {
                ChatSnippet? snippet = __instance.message[i];

                if (snippet.emojiIndex != -1)
                {
                    // Draw emoji
                    b.Draw(ChatBox.emojiTexture,
                        new Vector2(x + xPositionSoFar + 1f, y + yPositionSoFar - 4f),
                        new Rectangle(snippet.emojiIndex * 9 % ChatBox.emojiTexture.Width,
                            snippet.emojiIndex * 9 / ChatBox.emojiTexture.Width * 9, 9, 9),
                        Color.White * __instance.alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.99f);

                    // Emojis don't count towards character count for name bolding (names generally don't have emojis in this context? actually they can)
                    // But formattedUserName just returns the string name. If emojis are in the name, they are part of the string? 
                    // Stardew names are simple strings. The chat snippet splitting might separate them.
                    // Assuming for now name is text. If name has emoji it's complicated.
                }
                else if (snippet.message != null)
                {
                    if (snippet.message.Equals(Environment.NewLine))
                    {
                        // Handle newline
                        xPositionSoFar = 0f;
                        yPositionSoFar += font.MeasureString("(").Y;
                    }
                    else
                    {
                        // Logic to handle bolding part of the text
                        // We need to know which part of this snippet is within 'senderNameLength'

                        string currentText = snippet.message;
                        bool snippetHasUrl = UrlRegex.IsMatch(currentText);

                        if (snippetHasUrl)
                        {
                            // Fallback to URL logic, but we lose bolding for URLs (URLs in name? unlikely)
                            // Ideally we mix both, but complexity rises.
                            // Use the existing URL loop but add bolding check
                            MatchCollection matches = UrlRegex.Matches(currentText);
                            int lastIndex = 0;

                            foreach (Match match in matches)
                            {
                                if (match.Index > lastIndex)
                                {
                                    string beforeUrl = currentText.Substring(lastIndex, match.Index - lastIndex);
                                    DrawTextWithBold(b, font, beforeUrl, x + xPositionSoFar, y + yPositionSoFar,
                                        __instance.color * __instance.alpha, charactersDrawnSoFar, senderNameLength);
                                    xPositionSoFar += font.MeasureString(beforeUrl).X;
                                    charactersDrawnSoFar += beforeUrl.Length;
                                }

                                string url = match.Value;
                                Vector2 textSize = font.MeasureString(url);
                                Vector2 position = new(x + xPositionSoFar, y + yPositionSoFar);
                                Color urlColor = new Color(100, 149, 237) * __instance.alpha;

                                // Draw URL (never bold)
                                b.DrawString(font, url, position, urlColor, 0f, Vector2.Zero, 1f, SpriteEffects.None,
                                    0.99f);

                                Rectangle underline = new((int)position.X, (int)(position.Y + textSize.Y - 2),
                                    (int)textSize.X, 1);
                                b.Draw(Game1.staminaRect, underline, urlColor);

                                ActiveUrlRegions.Add(new UrlRegion
                                {
                                    Url = url,
                                    Bounds = new Rectangle((int)position.X, (int)position.Y, (int)textSize.X,
                                        (int)textSize.Y),
                                    Message = __instance
                                });

                                xPositionSoFar += textSize.X;
                                charactersDrawnSoFar += url.Length;
                                lastIndex = match.Index + match.Length;
                            }

                            if (lastIndex < currentText.Length)
                            {
                                string afterUrl = currentText[lastIndex..];
                                DrawTextWithBold(b, font, afterUrl, x + xPositionSoFar, y + yPositionSoFar,
                                    __instance.color * __instance.alpha, charactersDrawnSoFar, senderNameLength);
                                xPositionSoFar += font.MeasureString(afterUrl).X;
                                charactersDrawnSoFar += afterUrl.Length;
                            }
                        }
                        else
                        {
                            // Normal text, potentially bold
                            DrawTextWithBold(b, font, currentText, x + xPositionSoFar, y + yPositionSoFar,
                                __instance.color * __instance.alpha, charactersDrawnSoFar, senderNameLength);
                            xPositionSoFar +=
                                snippet.myLength; // DrawTextWithBold doesn't return width, rely on snippet
                            charactersDrawnSoFar += currentText.Length;
                        }
                    }
                }
                else
                {
                    xPositionSoFar += snippet.myLength;
                }

                // Handle text wrapping
                if (!(xPositionSoFar >= 888f)) continue;
                xPositionSoFar = 0f;
                yPositionSoFar += font.MeasureString("(").Y;
                if (__instance.message.Count > i + 1 &&
                    __instance.message[i + 1].message != null &&
                    __instance.message[i + 1].message.Equals(Environment.NewLine))
                    i++;
            }

            return false; // Skip original method
        }

        private static void DrawTextWithBold(SpriteBatch b, SpriteFont font, string text, float x, float y, Color color,
            int startIndex, int boldLength)
        {
            Color darkerGray = new Color(180, 180, 180, 255);
            if (startIndex >= boldLength)
            {
                // All normal
                b.DrawString(font, text, new Vector2(x, y), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
            }
            else if (startIndex + text.Length <= boldLength)
            {
                // All bold (Title/Sender) - Use colored shadow for emphasis
                Color shadowColor = Utility.MultiplyColor(color, darkerGray);
                Utility.drawTextWithColoredShadow(b, text, font, new Vector2(x, y), color, shadowColor, 1.09375f, -.5f,
                    2,
                    2,
                    3);
            }
            else
            {
                // Mixed
                int splitIndex = boldLength - startIndex;
                string boldPart = text.Substring(0, splitIndex);
                string normalPart = text.Substring(splitIndex);

                // Draw bold part
                Color shadowColor = Utility.MultiplyColor(color, darkerGray);
                Utility.drawTextWithColoredShadow(b, boldPart, font, new Vector2(x, y), color, shadowColor, 1.0625f,
                    .99f,
                    2, 2, 3);

                // Draw normal part
                float boldWidth = font.MeasureString(boldPart).X;
                b.DrawString(font, normalPart, new Vector2(x + boldWidth, y), color, 0f, Vector2.Zero, 1f,
                    SpriteEffects.None, 0.99f);
            }
        }
    }

    #endregion

    #region URL Clicking

    /// <summary>
    ///     Patch to handle clicks on URLs - only triggers on initial mouse press
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), "update")]
    public class UpdatePatch
    {
        private static void Postfix(ChatBox __instance)
        {
            // Check config for whether to allow clicking when chat is closed
            bool canClick = __instance.chatBox.Selected ||
                            (ModEntry.Instance?.Config.AllowUrlClickWhenChatClosed ?? true);

            if (!canClick) return;

            MouseState mouseState = Game1.input.GetMouseState();
            bool isMousePressed = mouseState.LeftButton == ButtonState.Pressed;

            // Only trigger on the initial press (transition from not pressed to pressed)
            if (isMousePressed && !_wasMousePressed)
            {
                Point mousePos = Game1.getMousePosition();
                int x = (int)(mousePos.X / Game1.options.zoomLevel);
                int y = (int)(mousePos.Y / Game1.options.zoomLevel);

                // Check if click is on any URL
                foreach (UrlRegion urlRegion in ActiveUrlRegions.Where(urlRegion => urlRegion.Bounds.Contains(x, y)))
                {
                    OpenUrl(urlRegion.Url);
                    Game1.playSound("drumkit6"); // Play a click sound
                    break;
                }
            }

            _wasMousePressed = isMousePressed;

            // Clean up old messages from cache periodically
            if (Game1.ticks % 60 == 0) // Every second
                CleanupOldMessages(__instance);
        }

        private static void CleanupOldMessages(ChatBox chatBox)
        {
            // Use reflection to access private messages field
            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField == null) return;
            if (messagesField.GetValue(chatBox) is not List<ChatMessage> messages) return;
            MessagesWithUrls.RemoveWhere(msg => !messages.Contains(msg));
            ActiveUrlRegions.RemoveAll(region => !messages.Contains(region.Message));
        }
    }

    /// <summary>
    ///     Patch to show pointer cursor when hovering over URLs
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), "draw")]
    public class DrawPatch
    {
        private static void Postfix(ChatBox __instance, SpriteBatch b)
        {
            // Check config for whether to show cursor when chat is closed
            bool canInteract = __instance.chatBox.Selected ||
                               (ModEntry.Instance?.Config.AllowUrlClickWhenChatClosed ?? true);

            if (!canInteract) return;

            Point mousePos = Game1.getMousePosition();
            int x = (int)(mousePos.X / Game1.options.zoomLevel);
            int y = (int)(mousePos.Y / Game1.options.zoomLevel);

            // Check if hovering over any URL
            if (!ActiveUrlRegions.Any(urlRegion => urlRegion.Bounds.Contains(x, y))) return;
            Game1.mouseCursor = Game1.cursor_gamepad_pointer;
        }
    }

    //Thinking we will need to override the bottom part, changing 888 to 864 or
    /* Base code from decompiled source:
    public void draw(SpriteBatch b, int x, int y)
    {
        float xPositionSoFar = 0f;
        float yPositionSoFar = 0f;
        for (int i = 0; i < message.Count; i++)
        {
            if (message[i].emojiIndex != -1)
            {
                b.Draw(ChatBox.emojiTexture, new Vector2((float)x + xPositionSoFar + 1f, (float)y + yPositionSoFar - 4f), new Rectangle(message[i].emojiIndex * 9 % ChatBox.emojiTexture.Width, message[i].emojiIndex * 9 / ChatBox.emojiTexture.Width * 9, 9, 9), Color.White * alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.99f);
            }
            else if (message[i].message != null)
            {
                if (message[i].message.Equals(Environment.NewLine))
                {
                    xPositionSoFar = 0f;
                    yPositionSoFar += ChatBox.messageFont(language).MeasureString("(").Y;
                }
                else
                {
                    b.DrawString(ChatBox.messageFont(language), message[i].message, new Vector2((float)x + xPositionSoFar, (float)y + yPositionSoFar), color * alpha, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                }
            }
            xPositionSoFar += message[i].myLength;
            if (xPositionSoFar >= 888f)
            {
                xPositionSoFar = 0f;
                yPositionSoFar += ChatBox.messageFont(language).MeasureString("(").Y;
                if (message.Count > i + 1 && message[i + 1].message != null && message[i + 1].message.Equals(Environment.NewLine))
                {
                    i++;
                }
            }
        }
    }
    */

    #endregion
}