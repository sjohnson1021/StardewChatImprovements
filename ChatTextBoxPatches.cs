using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;
using System.Collections.Generic;
using StardewValley;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Linq;


namespace ChatImprovements
{
#pragma warning disable CS8602 // Dereference of a possibly null reference
    /// <summary>
    /// Harmony patches for ChatTextBox to enable longer messages with scrolling and cursor control.
    /// </summary>
    internal class ChatTextBoxPatches
    {
        private static readonly Dictionary<ChatTextBox, int> SelectionStarts = new();
        private static readonly Dictionary<ChatTextBox, int> SelectionEnds = new();
        private static readonly Dictionary<ChatTextBox, string> FullTexts = new();
        private static readonly Dictionary<ChatTextBox, int> CursorIndices = new();
        private static readonly Dictionary<ChatTextBox, double> LastLeftPressTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastRightPressTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastHomePressTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastEndPressTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastLeftRepeatTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastRightRepeatTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastHomeRepeatTime = new();
        private static readonly Dictionary<ChatTextBox, double> LastEndRepeatTime = new();
        private static readonly Dictionary<ChatTextBox, float> ScrollOffsets = new();

        // Clipboard access via SDL2
        [DllImport("SDL2")]
        private static extern IntPtr SDL_GetClipboardText();
        [DllImport("SDL2")]
        private static extern int SDL_SetClipboardText(string text);
        [DllImport("SDL2")]
        private static extern void SDL_free(IntPtr mem);

        private static void SetWaylandClipboard(string text)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "wl-copy",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        using (var writer = process.StandardInput)
                        {
                            writer.Write(text);
                        }
                        process.WaitForExit();
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Instance?.Monitor.Log($"Wayland clipboard set error: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
                throw;
            }
        }

        private static string GetClipboardText()
        {
            try
            {
                IntPtr ptr = SDL_GetClipboardText();
                if (ptr == IntPtr.Zero) return "";
                string text = Marshal.PtrToStringUTF8(ptr) ?? "";
                SDL_free(ptr);
                return text;
            }
            catch (Exception ex)
            {
                ModEntry.Instance?.Monitor.Log($"Error getting clipboard text: {ex.Message}", StardewModdingAPI.LogLevel.Error);
                return "";
            }
        }

        private static void SetClipboardText(string text)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    SetWaylandClipboard(text);
                    return;
                }
                catch
                {
                    // Fall back to SDL2
                }
            }

            try
            {
                // Convert C# string (UTF-16) to UTF-8 byte array
                byte[] utf8Bytes = Encoding.UTF8.GetBytes(text + "\0");
                GCHandle handle = GCHandle.Alloc(utf8Bytes, GCHandleType.Pinned);
                try
                {
                    // Call using the pointer to the UTF-8 bytes
                    SDL_SetClipboardText_Internal(handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }
            }
            catch (Exception ex)
            {
                ModEntry.Instance?.Monitor.Log($"Error: {ex.Message}", StardewModdingAPI.LogLevel.Error);
            }
        }

        // Updated Import using IntPtr to ensure manual control
        [DllImport("SDL2", EntryPoint = "SDL_SetClipboardText")]
        private static extern int SDL_SetClipboardText_Internal(IntPtr text);

        private static int GetNextSegmentEnd(string text, int pos)
        {
            if (pos >= text.Length) return pos;

            // Check if we're at start of an emoji
            var emojiRange = GetEmojiToDelete(text, pos, isBackspace: false);
            if (emojiRange.start != -1)
            {
                return emojiRange.end;
            }

            // Normal logic
            if (char.IsWhiteSpace(text[pos]))
            {
                int end = pos;
                while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                return end;
            }

            if (char.IsLetterOrDigit(text[pos]))
            {
                int end = pos;
                while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
                return end;
            }

            // Symbol/punctuation — but check for emoji
            if (text[pos] == '[')
            {
                int close = text.IndexOf(']', pos);
                if (close != -1 && IsEmojiToken(text, pos, close))
                {
                    return close + 1;
                }
            }

            // Otherwise treat as symbol run
            int symbolEnd = pos;
            while (symbolEnd < text.Length &&
                !char.IsWhiteSpace(text[symbolEnd]) &&
                !char.IsLetterOrDigit(text[symbolEnd]))
            {
                symbolEnd++;
            }
            return symbolEnd;
        }

        private static int GetPrevSegmentStart(string text, int pos)
        {
            if (pos <= 0) return 0;

            // First, check if we're just after an emoji
            var emojiRange = GetEmojiToDelete(text, pos, isBackspace: true);
            if (emojiRange.start != -1)
            {
                return emojiRange.start; // jump to start of emoji
            }

            // Otherwise, do normal word logic, but skip over any emoji tokens
            int i = pos - 1;
            // Skip backwards over non-word chars (but include emojis as one)
            while (i >= 0 && !char.IsLetterOrDigit(text[i]))
            {
                // If we hit an emoji, jump to its start
                if (text[i] == ']')
                {
                    int open = -1;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (text[j] == '[')
                        {
                            open = j;
                            break;
                        }
                    }
                    if (open != -1 && IsEmojiToken(text, open, i))
                    {
                        return open;
                    }
                }
                i--;
            }

            // Now skip backwards over letters/digits
            while (i >= 0 && char.IsLetterOrDigit(text[i]))
            {
                i--;
            }

            return i + 1;
        }

        private static void InsertText(ChatTextBox instance, string text)
        {
            string fullText = FullTexts.GetValueOrDefault(instance, "");
            int selStart = SelectionStarts.GetValueOrDefault(instance, 0);
            int selEnd = SelectionEnds.GetValueOrDefault(instance, 0);
            int cursorIndex = CursorIndices.GetValueOrDefault(instance, fullText.Length);

            // If selection, replace it
            if (selStart != selEnd)
            {
                int start = Math.Min(selStart, selEnd);
                int length = Math.Abs(selEnd - selStart);
                fullText = fullText.Remove(start, length);
                cursorIndex = start;
            }

            // Check length
            if (fullText.Length + text.Length > ModEntry.Instance.Config.MaxMessageLength)
                return;

            // Insert
            fullText = fullText.Insert(cursorIndex, text);
            FullTexts[instance] = fullText;
            CursorIndices[instance] = cursorIndex + text.Length;
            SelectionStarts[instance] = cursorIndex + text.Length;
            SelectionEnds[instance] = cursorIndex + text.Length;
            RebuildFinalText(instance);
        }

        private static void DeleteSelection(ChatTextBox instance)
        {
            string fullText = FullTexts.GetValueOrDefault(instance, "");
            int selStart = SelectionStarts.GetValueOrDefault(instance, 0);
            int selEnd = SelectionEnds.GetValueOrDefault(instance, 0);

            if (selStart != selEnd)
            {
                int start = Math.Min(selStart, selEnd);
                int length = Math.Abs(selEnd - selStart);
                fullText = fullText.Remove(start, length);
                FullTexts[instance] = fullText;
                CursorIndices[instance] = start;
                SelectionStarts[instance] = start;
                SelectionEnds[instance] = start;
                RebuildFinalText(instance);
            }
        }

        private static void RebuildFinalText(ChatTextBox instance)
        {
            string fullText = FullTexts.GetValueOrDefault(instance, "");
            instance.finalText.Clear();
            instance.finalText.Add(new ChatSnippet(fullText, LocalizedContentManager.CurrentLanguageCode));
            instance.updateWidth();
        }

        private static void ResetScroll(ChatTextBox instance)
        {
            ScrollOffsets[instance] = 0f;
        }
        
        private static bool IsEmojiToken(string text, int open, int close)
        {
            if (open < 0 || close <= open || text[open] != '[' || text[close] != ']')
                return false;

            string inner = text.Substring(open + 1, close - open - 1);
            if (int.TryParse(inner, out int index))
            {
                return index >= 0 && index < EmojiMenu.totalEmojis;
            }
            return false;
        }
        /// <summary>
        /// Adjusts cursor position to avoid landing inside emoji notation brackets.
        /// If cursor would be inside [XXX], snaps to the boundary based on direction.
        /// </summary>
        private static int SnapCursorToEmojiBoundary(string fullText, int cursorPos, int direction)
        {
            if (string.IsNullOrEmpty(fullText) || cursorPos < 0 || cursorPos > fullText.Length)
                return cursorPos;

            // Parse to find emoji positions
            int pos = 0;
            while (pos < fullText.Length)
            {
                if (fullText[pos] == '[')
                {
                    int closeBracket = fullText.IndexOf(']', pos);
                    if (closeBracket != -1)
                    {
                        // Found an emoji [XXX] from pos to closeBracket
                        int emojiStart = pos;
                        int emojiEnd = closeBracket + 1;

                        // Check if cursor is inside this emoji
                        if (cursorPos > emojiStart && cursorPos < emojiEnd)
                        {
                            // Snap based on direction
                            // direction < 0 means moving left (snap to start)
                            // direction > 0 means moving right (snap to end)
                            // direction = 0 means initial positioning (snap to nearest)
                            if (direction < 0)
                            {
                                return emojiStart;
                            }
                            else if (direction > 0)
                            {
                                return emojiEnd;
                            }
                            else
                            {
                                // Snap to nearest boundary
                                int distToStart = cursorPos - emojiStart;
                                int distToEnd = emojiEnd - cursorPos;
                                return (distToStart < distToEnd) ? emojiStart : emojiEnd;
                            }
                        }

                        pos = closeBracket + 1;
                        continue;
                    }
                }
                pos++;
            }

            return cursorPos;
        }

        /// <summary>
        /// Determines if there's an emoji at the specified position that should be deleted as a whole unit.
        /// Returns the start and end indices of the emoji if found, or (-1, -1) if not.
        /// </summary>
        /// <param name="text">The full text content</param>
        /// <param name="position">The cursor position</param>
        /// <param name="isBackspace">True if handling backspace (deleting left), false for delete key (deleting right)</param>
        private static (int start, int end) GetEmojiToDelete(string text, int position, bool isBackspace)
        {
            if (string.IsNullOrEmpty(text) || position < 0 || position > text.Length)
                return (-1, -1);

            int pos = 0;
            while (pos < text.Length)
            {
                if (text[pos] == '[')
                {
                    int closeBracket = text.IndexOf(']', pos);
                    int nextOpenBracket = (pos + 1 < text.Length) ? text.IndexOf('[', pos + 1) : -1;

                    // Ensure we have a valid closing bracket and no nested/open brackets
                    if (closeBracket != -1 && (nextOpenBracket == -1 || nextOpenBracket > closeBracket))
                    {
                        // Extract content between brackets and validate it's a number
                        string content = text.Substring(pos + 1, closeBracket - pos - 1);
                        if (int.TryParse(content, out int emojiIndex))
                        {
                            int emojiStart = pos;
                            int emojiEnd = closeBracket + 1; // Include the closing bracket

                            // Determine if this emoji should be deleted based on cursor position and key pressed
                            if (isBackspace)
                            {
                                // For backspace: delete entire emoji if cursor is at end or inside it
                                if (position > emojiStart && position <= emojiEnd)
                                {
                                    return (emojiStart, emojiEnd);
                                }
                            }
                            else
                            {
                                // For delete key: delete entire emoji if cursor is at start or inside it
                                if (position >= emojiStart && position < emojiEnd)
                                {
                                    return (emojiStart, emojiEnd);
                                }
                            }
                        }

                        // Skip to after this emoji
                        pos = closeBracket + 1;
                        continue;
                    }
                }
                pos++;
            }

            return (-1, -1);
        }

        /// <summary>
        /// Patch for RecieveTextInput(string) to remove width limit and enforce character limit.
        /// </summary>
        [HarmonyPatch(typeof(ChatTextBox), "RecieveTextInput", typeof(string))]
        public class RecieveTextInputStringPatch
        {
            static bool Prefix(ChatTextBox __instance, string text)
            {
                InsertText(__instance, text);
                return false; // Skip original method
            }
        }

        /// <summary>
        /// Patch for RecieveCommandInput to handle backspace at cursor.
        /// </summary>
        [HarmonyPatch(typeof(ChatTextBox), "RecieveCommandInput", typeof(char))]
        public class RecieveCommandInputPatch
        {
            static bool Prefix(ChatTextBox __instance, char command)
            {
                if (__instance.Selected && command == '\b')
                {
                    string fullText = FullTexts.GetValueOrDefault(__instance, "");
                    int selStart = SelectionStarts.GetValueOrDefault(__instance, 0);
                    int selEnd = SelectionEnds.GetValueOrDefault(__instance, 0);
                    int cursorIndex = CursorIndices.GetValueOrDefault(__instance, fullText.Length);
                    KeyboardState keyState = Game1.input.GetKeyboardState();
                    bool ctrlDown = keyState.IsKeyDown(Keys.LeftControl) || keyState.IsKeyDown(Keys.RightControl);
                    ResetScroll(__instance);

                    if (selStart != selEnd)
                    {
                        DeleteSelection(__instance);
                    }
                    else if (cursorIndex > 0)
                    {
                        if (ctrlDown)
                        {
                            var emojiRange = GetEmojiToDelete(fullText, cursorIndex, isBackspace: true);
                            if (emojiRange.start != -1)
                            {
                                fullText = fullText.Remove(emojiRange.start, emojiRange.end - emojiRange.start);
                                CursorIndices[__instance] = emojiRange.start;
                                SelectionStarts[__instance] = emojiRange.start;
                                SelectionEnds[__instance] = emojiRange.start;
                            }
                            else
                            {
                                int segmentStart = GetPrevSegmentStart(fullText, cursorIndex);
                                fullText = fullText.Remove(segmentStart, cursorIndex - segmentStart);
                                CursorIndices[__instance] = segmentStart;
                                SelectionStarts[__instance] = segmentStart;
                                SelectionEnds[__instance] = segmentStart;
                            }
                            FullTexts[__instance] = fullText;
                            RebuildFinalText(__instance);
                        }
                        else
                        {
                            // Check for emoji to delete as whole unit
                            var emojiRange = GetEmojiToDelete(fullText, cursorIndex, isBackspace: true);
                            if (emojiRange.start != -1)
                            {
                                fullText = fullText.Remove(emojiRange.start, emojiRange.end - emojiRange.start);
                                CursorIndices[__instance] = emojiRange.start;
                                SelectionStarts[__instance] = emojiRange.start;
                                SelectionEnds[__instance] = emojiRange.start;
                            }
                            else
                            {
                                // Normal character deletion
                                fullText = fullText.Remove(cursorIndex - 1, 1);
                                CursorIndices[__instance] = cursorIndex - 1;
                                SelectionStarts[__instance] = cursorIndex - 1;
                                SelectionEnds[__instance] = cursorIndex - 1;
                            }
                            FullTexts[__instance] = fullText;
                            RebuildFinalText(__instance);
                        }
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Patch for receiveEmoji to bypass width limit when horizontal scrolling is enabled.
        /// </summary> 
        [HarmonyPatch(typeof(ChatTextBox), "receiveEmoji")]
        public class ReceiveEmojiPatch
        {
            static bool Prefix(ChatTextBox __instance, int emoji)
            {
                if (!ModEntry.Instance.Config.EnableHorizontalScrolling)
                {
                    return true; // Use original method
                }

                // Check against max message length instead of visual width
                string fullText = FullTexts.GetValueOrDefault(__instance, "");
                int cursorIndex = CursorIndices.GetValueOrDefault(__instance, fullText.Length);

                // An emoji is represented as "[X]" in plain text (3-4 chars typically)
                // Check if we have room for the emoji notation
                if (fullText.Length + 10 > ModEntry.Instance.Config.MaxMessageLength)
                {
                    return false; // Prevent adding emoji
                }

                // Add the emoji to finalText
                __instance.finalText.Add(new ChatSnippet(emoji));
                __instance.updateWidth();

                // Update our tracking
                string updatedText = ChatMessage.makeMessagePlaintext(__instance.finalText, false);
                FullTexts[__instance] = updatedText;
                CursorIndices[__instance] = updatedText.Length;
                SelectionStarts[__instance] = updatedText.Length;
                SelectionEnds[__instance] = updatedText.Length;

                return false; // Skip original method
            }
        }

        /// <summary>
        /// Patch for Draw to add horizontal scrolling.
        /// </summary>
        [HarmonyPatch(typeof(ChatTextBox), "Draw")]
        public class DrawPatch
        {
            private static readonly AccessTools.FieldRef<ChatTextBox, Texture2D> _textBoxTextureRef = AccessTools.FieldRefAccess<ChatTextBox, Texture2D>("_textBoxTexture");
            private static readonly AccessTools.FieldRef<ChatTextBox, Color> _textColorRef = AccessTools.FieldRefAccess<ChatTextBox, Color>("_textColor");

            static bool Prefix(ChatTextBox __instance, SpriteBatch spriteBatch, bool drawShadow)
            {
                if (!ModEntry.Instance.Config.EnableHorizontalScrolling)
                {
                    return true;
                }

                Texture2D textBoxTexture = _textBoxTextureRef(__instance);
                Color textColor = _textColorRef(__instance);
                bool showCursor = Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 >= 500.0;

                // Draw box background
                if (textBoxTexture != null)
                {
                    spriteBatch.Draw(textBoxTexture, new Rectangle(__instance.X, __instance.Y, 16, __instance.Height), new Rectangle(0, 0, 16, __instance.Height), Color.White);
                    spriteBatch.Draw(textBoxTexture, new Rectangle(__instance.X + 16, __instance.Y, __instance.Width - 32, __instance.Height), new Rectangle(16, 0, 4, __instance.Height), Color.White);
                    spriteBatch.Draw(textBoxTexture, new Rectangle(__instance.X + __instance.Width - 16, __instance.Y, 16, __instance.Height), new Rectangle(textBoxTexture.Bounds.Width - 16, 0, 16, __instance.Height), Color.White);
                }
                else
                {
                    Game1.drawDialogueBox(__instance.X - 32, __instance.Y - 112 + 10, __instance.Width + 80, __instance.Height, speaker: false, drawOnlyBox: true);
                }

                string fullText = FullTexts.GetValueOrDefault(__instance, "");
                int cursorIndex = CursorIndices.GetValueOrDefault(__instance, fullText.Length);
                int selStart = SelectionStarts.GetValueOrDefault(__instance, cursorIndex);
                int selEnd = SelectionEnds.GetValueOrDefault(__instance, cursorIndex);

                // Parse the fullText into snippets for proper emoji rendering
                ChatMessage parsedMessage = new ChatMessage();
                parsedMessage.parseMessageForEmoji(fullText);
                parsedMessage.color = ChatMessage.getColorFromName(Game1.player.defaultChatColor);
                parsedMessage.language = LocalizedContentManager.CurrentLanguageCode;

                var font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);
                float maxWidth = __instance.Width - 72f;
                float totalWidth = parsedMessage.message.Sum(s => s.myLength);
                float scrollOffset = ScrollOffsets.GetValueOrDefault(__instance, 0f);

                // Calculate cursor pixel position
                // ============ DO NOT TOUCH - CURSOR POSITION CALCULATION ============
                // Calculate cursor pixel position based on actual rendered snippets, not plain text
                float cursorPixel = 0f;
                if (cursorIndex > 0)
                {
                    int charCount = 0;
                    foreach (var snippet in parsedMessage.message)
                    {
                        if (snippet.emojiIndex != -1)
                        {
                            // Emoji is represented as "[X]" or "[XX]" or "[XXX]" in plain text
                            // Find how many chars this emoji takes in plain text
                            int emojiCharCount = snippet.emojiIndex.ToString().Length + 2; // digits + brackets

                            if (charCount + emojiCharCount <= cursorIndex)
                            {
                                cursorPixel += snippet.myLength; // Use actual emoji width (40px)
                                charCount += emojiCharCount;
                            }
                            else if (charCount < cursorIndex)
                            {
                                // Cursor is inside emoji notation - place at start of emoji
                                cursorPixel += snippet.myLength;
                                break;
                            }
                            else
                            {
                                break;
                            }
                        }
                        else if (snippet.message != null)
                        {
                            int snippetLength = snippet.message.Length;

                            if (charCount + snippetLength <= cursorIndex)
                            {
                                cursorPixel += snippet.myLength; // Use actual text width
                                charCount += snippetLength;
                            }
                            else if (charCount < cursorIndex)
                            {
                                // Cursor is partway through this snippet
                                int partialLength = cursorIndex - charCount;
                                cursorPixel += font.MeasureString(snippet.message.Substring(0, partialLength)).X;
                                break;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
                // ============ END DO NOT TOUCH SECTION ============

                // Update scroll to keep cursor visible
                float cursorViewPos = cursorPixel - scrollOffset;
                if (cursorViewPos > maxWidth)
                    scrollOffset = cursorPixel - maxWidth;
                else if (cursorViewPos < 0)
                    scrollOffset = Math.Max(0, cursorPixel);

                scrollOffset = Math.Clamp(scrollOffset, 0, Math.Max(0, totalWidth - maxWidth));
                ScrollOffsets[__instance] = scrollOffset;

                // Set up clipping rectangle
                Rectangle oldScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
                RasterizerState oldRasterizer = spriteBatch.GraphicsDevice.RasterizerState;

                spriteBatch.End();

                RasterizerState rasterizerState = new RasterizerState { ScissorTestEnable = true };
                Rectangle scissorRect = new Rectangle(__instance.X + 16, __instance.Y, __instance.Width - 72, __instance.Height);

                spriteBatch.GraphicsDevice.ScissorRectangle = scissorRect;
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, rasterizerState);

                // Draw selection highlight
                if (selStart != selEnd)
                {
                    int minSel = Math.Min(selStart, selEnd);
                    int maxSel = Math.Max(selStart, selEnd);
                    float selStartPixel = (minSel > 0) ? font.MeasureString(fullText.Substring(0, minSel)).X : 0f;
                    float selEndPixel = (maxSel > 0) ? font.MeasureString(fullText.Substring(0, maxSel)).X : 0f;
                    float textXHighlight = __instance.X + 16f - scrollOffset;
                    Rectangle highlightRect = new Rectangle((int)(textXHighlight + selStartPixel), __instance.Y + 8, (int)(selEndPixel - selStartPixel), 32);
                    Color selectionColor = new Color(0, 120, 215, 100); // Light blue semi-transparent
                    spriteBatch.Draw(Game1.staminaRect, highlightRect, selectionColor);
                }

                // Draw the parsed message (text and emojis)
                float currentX = __instance.X + 16f - scrollOffset;
                float yPos = __instance.Y + 12;
                foreach (var snippet in parsedMessage.message)
                {
                    if (snippet.emojiIndex != -1)
                    {
                        spriteBatch.Draw(ChatBox.emojiTexture, new Vector2(currentX + 1f, yPos - 4f), new Rectangle(snippet.emojiIndex * 9 % ChatBox.emojiTexture.Width, snippet.emojiIndex * 9 / ChatBox.emojiTexture.Width * 9, 9, 9), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.99f);
                    }
                    else if (snippet.message != null)
                    {
                        spriteBatch.DrawString(font, snippet.message, new Vector2(currentX, yPos), parsedMessage.color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                    }
                    currentX += snippet.myLength;
                }

                // Draw cursor
                if (showCursor && __instance.Selected)
                {
                    float cursorX = __instance.X + 16f + cursorPixel - scrollOffset;
                    spriteBatch.Draw(Game1.staminaRect,
                        new Rectangle((int)(cursorX - 1), __instance.Y + 8, 2, 32),
                        textColor);
                }

                // Restore graphics state
                spriteBatch.End();
                spriteBatch.GraphicsDevice.ScissorRectangle = oldScissor;
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, oldRasterizer);

                return false;
            }
        }

        /// <summary>
        /// Patch for ChatBox.receiveLeftClick to handle cursor positioning on click and emoji insertion.
        /// </summary>
        [HarmonyPatch(typeof(ChatBox), "receiveLeftClick")]
        public class ReceiveLeftClickPatch
        {
            static bool Prefix(ChatBox __instance, int x, int y, bool playSound)
            {
                if (!__instance.chatBox.Selected)
                {
                    return false;
                }
                if (__instance.emojiMenuIcon.containsPoint(x, y))
                {
                    __instance.choosingEmoji = !__instance.choosingEmoji;
                    Game1.playSound("shwip");
                    __instance.emojiMenuIcon.scale = 4f;
                    return false;
                }
                if (__instance.choosingEmoji && __instance.emojiMenu.isWithinBounds(x, y))
                {
                    __instance.emojiMenu.leftClick(x, y, __instance);
                    // Update fullText from finalText after emoji insertion
                    string updatedText = ChatMessage.makeMessagePlaintext(__instance.chatBox.finalText, false);
                    FullTexts[__instance.chatBox] = updatedText;
                    CursorIndices[__instance.chatBox] = updatedText.Length;
                    SelectionStarts[__instance.chatBox] = updatedText.Length;
                    SelectionEnds[__instance.chatBox] = updatedText.Length;
                    return false;
                }
                __instance.chatBox.Update();
                if (__instance.choosingEmoji)
                {
                    __instance.choosingEmoji = false;
                    __instance.emojiMenuIcon.scale = 4f;
                }
                if (__instance.isWithinBounds(x, y))
                {
                    __instance.chatBox.Selected = true;
                }
                // OLD PRE-FIXED EMOJI WIDTH // Handle cursor positioning
                // if (__instance.chatBox.Selected && __instance.isWithinBounds(x, y) && !__instance.emojiMenuIcon.containsPoint(x, y) && (!__instance.choosingEmoji || !__instance.emojiMenu.isWithinBounds(x, y)))
                {
                    string fullText = FullTexts.GetValueOrDefault(__instance.chatBox, "");
                    float scrollOffset = ScrollOffsets.GetValueOrDefault(__instance.chatBox, 0f);
                    float clickX = x - __instance.chatBox.X - 16 + scrollOffset; // Account for scroll offset
                    float currentX = 0;
                    int newCursor = 0;

                    var font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);

                    for (int i = 0; i < fullText.Length; i++)
                    {
                        float charWidth = font.MeasureString(fullText[i].ToString()).X;
                        if (currentX + charWidth / 2 > clickX)
                        {
                            newCursor = i;
                            break;
                        }
                        currentX += charWidth;
                        newCursor = i + 1;
                    }

                    KeyboardState keyState = Game1.input.GetKeyboardState();
                    bool shiftDown = keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);

                    if (shiftDown)
                    {
                        // Extend selection
                        SelectionEnds[__instance.chatBox] = newCursor;
                    }
                    else
                    {
                        // Clear selection and set cursor
                        CursorIndices[__instance.chatBox] = newCursor;
                        SelectionStarts[__instance.chatBox] = newCursor;
                        SelectionEnds[__instance.chatBox] = newCursor;
                    }
                }
                // Handle cursor positioning
                if (__instance.chatBox.Selected && __instance.isWithinBounds(x, y) && !__instance.emojiMenuIcon.containsPoint(x, y) && (!__instance.choosingEmoji || !__instance.emojiMenu.isWithinBounds(x, y)))
                {
                    string fullText = FullTexts.GetValueOrDefault(__instance.chatBox, "");
                    float scrollOffset = ScrollOffsets.GetValueOrDefault(__instance.chatBox, 0f);
                    float clickX = x - __instance.chatBox.X - 16 + scrollOffset;

                    // Parse message to get actual rendered snippets
                    ChatMessage parsedMessage = new ChatMessage();
                    parsedMessage.parseMessageForEmoji(fullText);

                    float currentX = 0;
                    int newCursor = 0;
                    int charCount = 0;
                    var font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);

                    foreach (var snippet in parsedMessage.message)
                    {
                        if (snippet.emojiIndex != -1)
                        {
                            if (currentX + snippet.myLength / 2 > clickX)
                            {
                                newCursor = charCount;
                                break;
                            }
                            currentX += snippet.myLength;
                            charCount += snippet.emojiIndex.ToString().Length + 2; // [XXX] notation
                            newCursor = charCount;
                        }
                        else if (snippet.message != null)
                        {
                            for (int i = 0; i < snippet.message.Length; i++)
                            {
                                float charWidth = font.MeasureString(snippet.message[i].ToString()).X;
                                if (currentX + charWidth / 2 > clickX)
                                {
                                    newCursor = charCount + i;
                                    goto FoundPosition;
                                }
                                currentX += charWidth;
                            }
                            charCount += snippet.message.Length;
                            newCursor = charCount;
                        }
                    }

                FoundPosition:
                    KeyboardState keyState = Game1.input.GetKeyboardState();
                    bool shiftDown = keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);

                    // Snap cursor to emoji boundary (neutral direction for clicks)
                    newCursor = SnapCursorToEmojiBoundary(fullText, newCursor, 0);

                    if (shiftDown)
                    {
                        SelectionEnds[__instance.chatBox] = newCursor;
                    }
                    else
                    {
                        CursorIndices[__instance.chatBox] = newCursor;
                        SelectionStarts[__instance.chatBox] = newCursor;
                        SelectionEnds[__instance.chatBox] = newCursor;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Patch for ChatBox.receiveKeyPress to handle cursor movement.
        /// </summary>
        [HarmonyPatch(typeof(ChatBox), "receiveKeyPress")]
        public class ReceiveKeyPressPatch
        {
            static bool Prefix(ChatBox __instance, Keys key)
            {
                if (__instance.chatBox.Selected && ModEntry.Instance.Config.EnableCursorControl)
                {
                    string fullText = FullTexts.GetValueOrDefault(__instance.chatBox, "");
                    int cursorIndex = CursorIndices.GetValueOrDefault(__instance.chatBox, fullText.Length);
                    int selStart = SelectionStarts.GetValueOrDefault(__instance.chatBox, cursorIndex);
                    int selEnd = SelectionEnds.GetValueOrDefault(__instance.chatBox, cursorIndex);
                    double currentTime = Game1.currentGameTime.TotalGameTime.TotalSeconds;
                    KeyboardState keyState = Game1.input.GetKeyboardState();
                    bool shiftDown = keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);
                    bool ctrlDown = keyState.IsKeyDown(Keys.LeftControl) || keyState.IsKeyDown(Keys.RightControl);

                    int newCursor = cursorIndex;
                    bool handled = false;

                    // Handle Ctrl+A (Select All)
                    if (ctrlDown && key == Keys.A)
                    {
                        SelectionStarts[__instance.chatBox] = 0;
                        SelectionEnds[__instance.chatBox] = fullText.Length;
                        CursorIndices[__instance.chatBox] = fullText.Length;
                        return false;
                    }

                    // Handle Ctrl+C (Copy)
                    if (ctrlDown && key == Keys.C)
                    {
                        if (selStart != selEnd)
                        {
                            int minSel = Math.Min(selStart, selEnd);
                            int maxSel = Math.Max(selStart, selEnd);
                            string selected = fullText.Substring(minSel, maxSel - minSel);
                            SetClipboardText(selected);
                        }
                        return false;
                    }

                    // Handle Ctrl+X (Cut)
                    if (ctrlDown && key == Keys.X)
                    {
                        if (selStart != selEnd)
                        {
                            int minSel = Math.Min(selStart, selEnd);
                            int maxSel = Math.Max(selStart, selEnd);
                            string selected = fullText.Substring(minSel, maxSel - minSel);
                            SetClipboardText(selected);
                            DeleteSelection(__instance.chatBox);
                        }
                        return false;
                    }

                    // Handle Ctrl+V (Paste)
                    if (ctrlDown && key == Keys.V)
                    {
                        string clipboard = GetClipboardText();
                        if (!string.IsNullOrEmpty(clipboard))
                        {
                            InsertText(__instance.chatBox, clipboard);
                        }
                        return false;
                    }

                    // Handle cursor movement
                    if (key == Keys.Left)
                    {
                        if (ctrlDown)
                        {
                            newCursor = GetPrevSegmentStart(fullText, cursorIndex);
                        }
                        else
                        {
                            if (cursorIndex > 0)
                                newCursor = cursorIndex - 1;
                        }
                        // Snap to emoji boundary when moving left
                        newCursor = SnapCursorToEmojiBoundary(fullText, newCursor, -1);
                        handled = true;
                    }
                    else if (key == Keys.Right)
                    {
                        if (ctrlDown)
                        {
                            newCursor = GetNextSegmentEnd(fullText, cursorIndex);
                        }
                        else
                        {
                            if (cursorIndex < fullText.Length)
                                newCursor = cursorIndex + 1;
                        }
                        // Snap to emoji boundary when moving right
                        newCursor = SnapCursorToEmojiBoundary(fullText, newCursor, 1);
                        handled = true;
                    }
                    else if (key == Keys.Home)
                    {
                        newCursor = 0;
                        handled = true;
                    }
                    else if (key == Keys.End)
                    {
                        newCursor = fullText.Length;
                        handled = true;
                    }
                    else if (key == Keys.Delete)
                    {
                        // Handle Delete key
                        ResetScroll(__instance.chatBox);
                        if (selStart != selEnd)
                        {
                            DeleteSelection(__instance.chatBox);
                        }
                        else if (cursorIndex < fullText.Length)
                        {
                            if (ctrlDown)
                            {
                                // First, check if we're at the start of an emoji
                                var emojiRange = GetEmojiToDelete(fullText, cursorIndex, isBackspace: false);
                                if (emojiRange.start != -1)
                                {
                                    // Delete entire emoji
                                    fullText = fullText.Remove(emojiRange.start, emojiRange.end - emojiRange.start);
                                }
                                else
                                {
                                    // Otherwise, delete by word
                                    int segmentEnd = GetNextSegmentEnd(fullText, cursorIndex);
                                    fullText = fullText.Remove(cursorIndex, segmentEnd - cursorIndex);
                                }
                            }
                            else
                            {
                                // Normal delete: already handles emoji correctly
                                var emojiRange = GetEmojiToDelete(fullText, cursorIndex, isBackspace: false);
                                if (emojiRange.start != -1)
                                {
                                    fullText = fullText.Remove(emojiRange.start, emojiRange.end - emojiRange.start);
                                }
                                else
                                {
                                    fullText = fullText.Remove(cursorIndex, 1);
                                }
                            }
                            FullTexts[__instance.chatBox] = fullText;
                            RebuildFinalText(__instance.chatBox);
                        }
                        return false;
                    }
                    if (handled)
                    {
                        if (shiftDown)
                        {
                            // Extend selection
                            if (selStart == selEnd)
                            {
                                SelectionStarts[__instance.chatBox] = cursorIndex;
                            }
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        else
                        {
                            // Clear selection
                            SelectionStarts[__instance.chatBox] = newCursor;
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        CursorIndices[__instance.chatBox] = newCursor;

                        // Track press time for repeat
                        if (key == Keys.Left)
                            LastLeftPressTime[__instance.chatBox] = currentTime;
                        else if (key == Keys.Right)
                            LastRightPressTime[__instance.chatBox] = currentTime;
                        else if (key == Keys.Home)
                            LastHomePressTime[__instance.chatBox] = currentTime;
                        else if (key == Keys.End)
                            LastEndPressTime[__instance.chatBox] = currentTime;

                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Patch for ChatBox.textBoxEnter to enforce message length on send.
        /// </summary>
        [HarmonyPatch(typeof(ChatBox), "textBoxEnter", typeof(TextBox))]
        public class TextBoxEnterPatch
        {
            static bool Prefix(ChatBox __instance, TextBox sender)
            {
                if (sender is ChatTextBox chatTextBox)
                {
                    string fullText = FullTexts.GetValueOrDefault(chatTextBox, "");
                    if (fullText.Length > ModEntry.Instance.Config.MaxMessageLength)
                    {
                        __instance.addErrorMessage("Message too long! Maximum " + ModEntry.Instance.Config.MaxMessageLength + " characters.");
                        return false; // Prevent sending
                    }
                }
                return true;
            }

            static void Postfix(TextBox sender)
            {
                if (sender is ChatTextBox chatTextBox)
                {
                    // Clear the stored text after successful send
                    FullTexts[chatTextBox] = "";
                    CursorIndices[chatTextBox] = 0;
                    SelectionStarts[chatTextBox] = 0;
                    SelectionEnds[chatTextBox] = 0;
                    ResetScroll(chatTextBox);
                    // Also clear the finalText
                    chatTextBox.finalText.Clear();
                    chatTextBox.finalText.Add(new ChatSnippet("", LocalizedContentManager.CurrentLanguageCode));
                    chatTextBox.updateWidth();
                }
            }
        }

        /// <summary>
        /// Patch for ChatBox.update to handle key repeat for cursor movement.
        /// </summary>
        [HarmonyPatch(typeof(ChatBox), "update", typeof(GameTime))]
        public class UpdatePatch
        {
            static void Postfix(ChatBox __instance, GameTime time)
            {
                if (!__instance.chatBox.Selected || !ModEntry.Instance.Config.EnableCursorControl)
                    return;

                string fullText = FullTexts.GetValueOrDefault(__instance.chatBox, "");
                int cursorIndex = CursorIndices.GetValueOrDefault(__instance.chatBox, fullText.Length);
                double currentTime = time.TotalGameTime.TotalSeconds;
                KeyboardState keyState = Game1.input.GetKeyboardState();
                bool shiftDown = keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);
                bool ctrlDown = keyState.IsKeyDown(Keys.LeftControl) || keyState.IsKeyDown(Keys.RightControl);

                if (keyState.IsKeyDown(Keys.Left))
                {
                    double lastPress = LastLeftPressTime.GetValueOrDefault(__instance.chatBox, 0);
                    double initialDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
                    double repeatDelay = ModEntry.Instance.Config.KeyRepeatDelay;
                    double timeSincePress = currentTime - lastPress;
                    double lastRepeat = LastLeftRepeatTime.GetValueOrDefault(__instance.chatBox, 0);

                    if (timeSincePress >= initialDelay && currentTime - lastRepeat >= repeatDelay)
                    {
                        int newCursor = ctrlDown ? GetPrevSegmentStart(fullText, cursorIndex) : (cursorIndex > 0 ? cursorIndex - 1 : cursorIndex);
                        newCursor = SnapCursorToEmojiBoundary(fullText, newCursor, -1); 
                        if (newCursor != cursorIndex)
                        {
                            if (shiftDown)
                            {
                                if (SelectionStarts[__instance.chatBox] == SelectionEnds[__instance.chatBox])
                                    SelectionStarts[__instance.chatBox] = cursorIndex;
                                SelectionEnds[__instance.chatBox] = newCursor;
                            }
                            else
                            {
                                SelectionStarts[__instance.chatBox] = newCursor;
                                SelectionEnds[__instance.chatBox] = newCursor;
                            }
                            CursorIndices[__instance.chatBox] = newCursor;
                            LastLeftRepeatTime[__instance.chatBox] = currentTime;
                        }
                    }
                }
                else
                {
                    LastLeftPressTime.Remove(__instance.chatBox);
                    LastLeftRepeatTime.Remove(__instance.chatBox);
                }

                // Handle Right arrow repeat
                if (keyState.IsKeyDown(Keys.Right))
                {
                    double lastPress = LastRightPressTime.GetValueOrDefault(__instance.chatBox, 0);
                    double initialDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
                    double repeatDelay = ModEntry.Instance.Config.KeyRepeatDelay;
                    double timeSincePress = currentTime - lastPress;
                    double lastRepeat = LastRightRepeatTime.GetValueOrDefault(__instance.chatBox, 0);

                    if (timeSincePress >= initialDelay && currentTime - lastRepeat >= repeatDelay)
                    {
                        int newCursor = ctrlDown ? GetNextSegmentEnd(fullText, cursorIndex) : (cursorIndex < fullText.Length ? cursorIndex + 1 : cursorIndex);
                        newCursor = SnapCursorToEmojiBoundary(fullText, newCursor, 1); 
                        if (newCursor != cursorIndex)
                        {
                            if (shiftDown)
                            {
                                if (SelectionStarts[__instance.chatBox] == SelectionEnds[__instance.chatBox])
                                    SelectionStarts[__instance.chatBox] = cursorIndex;
                                SelectionEnds[__instance.chatBox] = newCursor;
                            }
                            else
                            {
                                SelectionStarts[__instance.chatBox] = newCursor;
                                SelectionEnds[__instance.chatBox] = newCursor;
                            }
                            CursorIndices[__instance.chatBox] = newCursor;
                            LastRightRepeatTime[__instance.chatBox] = currentTime;
                        }
                    }
                }
                else
                {
                    LastRightPressTime.Remove(__instance.chatBox);
                    LastRightRepeatTime.Remove(__instance.chatBox);
                }

                // Handle Home repeat
                if (keyState.IsKeyDown(Keys.Home))
                {
                    double lastPress = LastHomePressTime.GetValueOrDefault(__instance.chatBox, 0);
                    double initialDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
                    double repeatDelay = ModEntry.Instance.Config.KeyRepeatDelay;
                    double timeSincePress = currentTime - lastPress;
                    double lastRepeat = LastHomeRepeatTime.GetValueOrDefault(__instance.chatBox, 0);

                    if (timeSincePress >= initialDelay && currentTime - lastRepeat >= repeatDelay)
                    {
                        int newCursor = 0;
                        if (shiftDown)
                        {
                            if (SelectionStarts[__instance.chatBox] == SelectionEnds[__instance.chatBox])
                                SelectionStarts[__instance.chatBox] = cursorIndex;
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        else
                        {
                            SelectionStarts[__instance.chatBox] = newCursor;
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        CursorIndices[__instance.chatBox] = newCursor;
                        LastHomeRepeatTime[__instance.chatBox] = currentTime;
                    }
                }
                else
                {
                    LastHomePressTime.Remove(__instance.chatBox);
                    LastHomeRepeatTime.Remove(__instance.chatBox);
                }

                // Handle End repeat
                if (keyState.IsKeyDown(Keys.End))
                {
                    double lastPress = LastEndPressTime.GetValueOrDefault(__instance.chatBox, 0);
                    double initialDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
                    double repeatDelay = ModEntry.Instance.Config.KeyRepeatDelay;
                    double timeSincePress = currentTime - lastPress;
                    double lastRepeat = LastEndRepeatTime.GetValueOrDefault(__instance.chatBox, 0);

                    if (timeSincePress >= initialDelay && currentTime - lastRepeat >= repeatDelay)
                    {
                        int newCursor = fullText.Length;
                        if (shiftDown)
                        {
                            if (SelectionStarts[__instance.chatBox] == SelectionEnds[__instance.chatBox])
                                SelectionStarts[__instance.chatBox] = cursorIndex;
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        else
                        {
                            SelectionStarts[__instance.chatBox] = newCursor;
                            SelectionEnds[__instance.chatBox] = newCursor;
                        }
                        CursorIndices[__instance.chatBox] = newCursor;
                        LastEndRepeatTime[__instance.chatBox] = currentTime;
                    }
                }
                else
                {
                    LastEndPressTime.Remove(__instance.chatBox);
                    LastEndRepeatTime.Remove(__instance.chatBox);
                }
            }
        }
#pragma warning restore CS8602
    }
}