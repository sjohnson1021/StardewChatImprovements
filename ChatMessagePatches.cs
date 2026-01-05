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
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
                // Windows
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                // Linux
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                // macOS
                Process.Start("open", url);
            else
                // Fallback for other platforms
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Failed to open URL: {ex.Message}", LogLevel.Error);
            Game1.addHUDMessage(new HUDMessage("Failed to open URL", HUDMessage.error_type));
        }
    }

    #endregion

    private class UrlRegion
    {
        public Rectangle Bounds;
        public ChatMessage? Message;
        public string Url = "";
    }

    #region URL Rendering

    /// <summary>
    ///     Patch to render URLs with special formatting and track their bounds
    ///     Detects URLs during drawing rather than parsing to avoid myLength issues
    /// </summary>
    [HarmonyPatch(typeof(ChatMessage), "draw")]
    public class DrawMessagePatch
    {
        private static bool Prefix(ChatMessage __instance, SpriteBatch b, int x, int y)
        {
            // Check if this message has URLs (using cache or scanning)
            bool hasUrls = MessagesWithUrls.Contains(__instance);

            if (!hasUrls)
                // Not in cache - scan for URLs
                foreach (ChatSnippet? snippet in __instance.message)
                {
                    if (snippet.message == null || !UrlRegex.IsMatch(snippet.message)) continue;
                    hasUrls = true;
                    MessagesWithUrls.Add(__instance);
                    break;
                }

            if (!hasUrls) return true; // Use original method

            // Clear URL regions for this message
            ActiveUrlRegions.RemoveAll(r => r.Message == __instance);

            float xPositionSoFar = 0f;
            float yPositionSoFar = 0f;
            SpriteFont? font = ChatBox.messageFont(__instance.language);

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
                        // Check if this snippet contains URL(s)
                        MatchCollection matches = UrlRegex.Matches(snippet.message);

                        if (matches.Count > 0)
                        {
                            // This text contains URLs - draw it piece by piece
                            int lastIndex = 0;

                            foreach (Match match in matches)
                            {
                                // Draw text before URL (if any)
                                if (match.Index > lastIndex)
                                {
                                    string beforeUrl = snippet.message.Substring(lastIndex, match.Index - lastIndex);
                                    b.DrawString(font, beforeUrl,
                                        new Vector2(x + xPositionSoFar, y + yPositionSoFar),
                                        __instance.color * __instance.alpha, 0f, Vector2.Zero, 1f, SpriteEffects.None,
                                        0.99f);
                                    xPositionSoFar += font.MeasureString(beforeUrl).X;
                                }

                                // Draw URL with special formatting
                                string url = match.Value;
                                Vector2 textSize = font.MeasureString(url);
                                Vector2 position = new(x + xPositionSoFar, y + yPositionSoFar);

                                // Draw URL in a different color (light blue)
                                Color urlColor = new Color(100, 149, 237) * __instance.alpha; // Cornflower blue
                                b.DrawString(font, url, position, urlColor, 0f, Vector2.Zero, 1f, SpriteEffects.None,
                                    0.99f);

                                // Draw underline
                                Rectangle underline = new(
                                    (int)position.X,
                                    (int)(position.Y + textSize.Y - 2),
                                    (int)textSize.X,
                                    1
                                );
                                b.Draw(Game1.staminaRect, underline, urlColor);

                                // Track this URL region for clicking
                                ActiveUrlRegions.Add(new UrlRegion
                                {
                                    Url = url,
                                    Bounds = new Rectangle(
                                        (int)position.X,
                                        (int)position.Y,
                                        (int)textSize.X,
                                        (int)textSize.Y
                                    ),
                                    Message = __instance
                                });

                                xPositionSoFar += textSize.X;
                                lastIndex = match.Index + match.Length;
                            }

                            // Draw remaining text after last URL (if any)
                            if (lastIndex < snippet.message.Length)
                            {
                                string afterUrl = snippet.message[lastIndex..];
                                b.DrawString(font, afterUrl,
                                    new Vector2(x + xPositionSoFar, y + yPositionSoFar),
                                    __instance.color * __instance.alpha, 0f, Vector2.Zero, 1f, SpriteEffects.None,
                                    0.99f);
                                xPositionSoFar += font.MeasureString(afterUrl).X;
                            }
                        }
                        else
                        {
                            // Draw normal text (no URLs)
                            b.DrawString(font, snippet.message,
                                new Vector2(x + xPositionSoFar, y + yPositionSoFar),
                                __instance.color * __instance.alpha, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                            xPositionSoFar += snippet.myLength;
                        }
                    }
                }
                else
                {
                    // Advance by the snippet's length if we didn't draw anything
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

    #endregion
}