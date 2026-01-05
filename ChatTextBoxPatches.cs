using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;
#pragma warning disable CS8602
internal class ChatTextBoxPatches
{
    #region State Tracking

    private static readonly Dictionary<ChatTextBox, TextBoxState> States = new();

    private class TextBoxState
    {
        public int CursorIndex;
        public string FullText = "";
        public bool IsDragging, WasMousePressed;
        public double LastLeftPress, LastRightPress, LastHomePress, LastEndPress;
        public double LastLeftRepeat, LastRightRepeat, LastHomeRepeat, LastEndRepeat;
        public float ScrollOffset;
        public int SelectionEnd;
        public int SelectionStart;
    }

    private static TextBoxState GetState(ChatTextBox box)
    {
        if (States.TryGetValue(box, out TextBoxState? state)) return state;
        state = new TextBoxState();
        States[box] = state;
        return state;
    }

    #endregion

    #region Clipboard

    [DllImport("SDL2")]
    private static extern IntPtr SDL_GetClipboardText();

    [DllImport("SDL2")]
    private static extern void SDL_free(IntPtr mem);

    [DllImport("SDL2", EntryPoint = "SDL_SetClipboardText")]
    private static extern int SDL_SetClipboardText_Internal(IntPtr text);

    private static string GetClipboard()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                try
                {
                    Process? p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "wl-paste",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null)
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        return output;
                    }
                }
                catch
                {
                    /* Fall back to SDL2 */
                }

            IntPtr ptr = SDL_GetClipboardText();
            if (ptr == IntPtr.Zero) return "";
            string text = Marshal.PtrToStringUTF8(ptr) ?? "";
            SDL_free(ptr);
            return text;
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Clipboard error: {ex.Message}", LogLevel.Error);
            return "";
        }
    }

    private static void SetClipboard(string text)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                try
                {
                    Process? p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "wl-copy",
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null)
                    {
                        p.StandardInput.Write(text);
                        p.StandardInput.Close();
                        p.WaitForExit();
                        return;
                    }
                }
                catch
                {
                    /* Fall back to SDL2 */
                }

            byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
            GCHandle handle = GCHandle.Alloc(utf8, GCHandleType.Pinned);
            try
            {
                SDL_SetClipboardText_Internal(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            ModEntry.Instance?.Monitor.Log($"Clipboard error: {ex.Message}", LogLevel.Error);
        }
    }

    #endregion

    #region Text Navigation

    private static int GetNextSegmentEnd(string text, int pos)
    {
        if (pos >= text.Length) return pos;
        char c = text[pos];

        if (char.IsWhiteSpace(c))
            return AdvanceWhile(text, pos, char.IsWhiteSpace);
        return char.IsLetterOrDigit(c)
            ? AdvanceWhile(text, pos, char.IsLetterOrDigit)
            : AdvanceWhile(text, pos, ch => !char.IsWhiteSpace(ch) && !char.IsLetterOrDigit(ch));
    }

    private static int GetPrevSegmentStart(string text, int pos)
    {
        if (pos <= 0) return 0;
        char c = text[pos - 1];

        if (char.IsWhiteSpace(c))
            return RetreatWhile(text, pos - 1, char.IsWhiteSpace);
        return char.IsLetterOrDigit(c)
            ? RetreatWhile(text, pos - 1, char.IsLetterOrDigit)
            : RetreatWhile(text, pos - 1, ch => !char.IsWhiteSpace(ch) && !char.IsLetterOrDigit(ch));
    }

    private static int AdvanceWhile(string text, int pos, Func<char, bool> predicate)
    {
        while (pos < text.Length && predicate(text[pos])) pos++;
        return pos;
    }

    private static int RetreatWhile(string text, int pos, Func<char, bool> predicate)
    {
        while (pos > 0 && predicate(text[pos - 1])) pos--;
        return pos;
    }

    #endregion

    #region Emoji Handling

    private static int SnapCursorToEmojiBoundary(string text, int cursor, int direction)
    {
        if (string.IsNullOrEmpty(text) || cursor < 0 || cursor > text.Length) return cursor;

        int pos = 0;
        while (pos < text.Length)
            if (text[pos] == '[')
            {
                int close = text.IndexOf(']', pos);
                if (close != -1 && cursor > pos && cursor < close + 1)
                    return direction < 0 ? pos :
                        direction > 0 ? close + 1 :
                        cursor - pos < close + 1 - cursor ? pos : close + 1;
                pos = close != -1 ? close + 1 : pos + 1;
            }
            else
            {
                pos++;
            }

        return cursor;
    }

    private static (int start, int end) GetEmojiToDelete(string text, int pos, bool isBackspace)
    {
        if (string.IsNullOrEmpty(text) || pos < 0 || pos > text.Length) return (-1, -1);

        int idx = 0;
        while (idx < text.Length)
        {
            if (text[idx] == '[')
            {
                int close = text.IndexOf(']', idx);
                int nextOpen = idx + 1 < text.Length ? text.IndexOf('[', idx + 1) : -1;

                if (close != -1 && (nextOpen == -1 || nextOpen > close))
                {
                    string content = text.Substring(idx + 1, close - idx - 1);
                    if (int.TryParse(content, out _))
                    {
                        int start = idx, end = close + 1;
                        if ((isBackspace && pos > start && pos <= end) ||
                            (!isBackspace && pos >= start && pos < end))
                            return (start, end);
                    }

                    idx = close + 1;
                    continue;
                }
            }

            idx++;
        }

        return (-1, -1);
    }

    #endregion

    #region Text Operations

    private static void InsertText(ChatTextBox box, string text)
    {
        TextBoxState s = GetState(box);

        // Replace selection if exists
        if (s.SelectionStart != s.SelectionEnd)
        {
            int start = Math.Min(s.SelectionStart, s.SelectionEnd);
            int length = Math.Abs(s.SelectionEnd - s.SelectionStart);
            s.FullText = s.FullText.Remove(start, length);
            s.CursorIndex = start;
        }

        if (s.FullText.Length + text.Length > ModEntry.Instance.Config.MaxMessageLength)
            return;

        s.FullText = s.FullText.Insert(s.CursorIndex, text);
        s.CursorIndex += text.Length;
        s.SelectionStart = s.SelectionEnd = s.CursorIndex;
        RebuildText(box, s);
    }

    private static void DeleteSelection(ChatTextBox box)
    {
        TextBoxState s = GetState(box);
        if (s.SelectionStart == s.SelectionEnd) return;

        int start = Math.Min(s.SelectionStart, s.SelectionEnd);
        int length = Math.Abs(s.SelectionEnd - s.SelectionStart);
        s.FullText = s.FullText.Remove(start, length);
        s.CursorIndex = s.SelectionStart = s.SelectionEnd = start;
        RebuildText(box, s);
    }

    private static void RebuildText(ChatTextBox box, TextBoxState s)
    {
        box.finalText.Clear();
        box.finalText.Add(new ChatSnippet(s.FullText, LocalizedContentManager.CurrentLanguageCode));
        box.updateWidth();
    }

    private static void UpdateSelection(TextBoxState s, int newCursor, bool shift)
    {
        s.SelectionStart = shift switch
        {
            true when s.SelectionStart == s.SelectionEnd => s.CursorIndex,
            false => newCursor,
            _ => s.SelectionStart
        };

        s.SelectionEnd = s.CursorIndex = newCursor;
    }

    #endregion

    #region Patches

    [HarmonyPatch(typeof(ChatTextBox), "RecieveTextInput", typeof(string))]
    public class RecieveTextInputStringPatch
    {
        private static bool Prefix(ChatTextBox __instance, string text)
        {
            InsertText(__instance, text);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatTextBox), "receiveEmoji")]
    public class ReceiveEmojiPatch
    {
        private static bool Prefix(ChatTextBox __instance, int emoji)
        {
            if (!ModEntry.Instance.Config.EnableHorizontalScrolling) return true;

            TextBoxState s = GetState(__instance);
            if (s.FullText.Length + 10 > ModEntry.Instance.Config.MaxMessageLength) return false;

            __instance.finalText.Add(new ChatSnippet(emoji));
            __instance.updateWidth();

            s.FullText = ChatMessage.makeMessagePlaintext(__instance.finalText, false);
            s.CursorIndex = s.SelectionStart = s.SelectionEnd = s.FullText.Length;
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatTextBox), "RecieveCommandInput", typeof(char))]
    public class RecieveCommandInputPatch
    {
        private static bool Prefix(ChatTextBox __instance, char command)
        {
            if (!__instance.Selected || command != '\b') return true;

            TextBoxState s = GetState(__instance);
            KeyboardState keys = Game1.input.GetKeyboardState();
            bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
            s.ScrollOffset = 0;

            if (s.SelectionStart != s.SelectionEnd)
            {
                DeleteSelection(__instance);
            }
            else if (s.CursorIndex > 0)
            {
                if (ctrl)
                {
                    (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, true);

                    if (emoji.start != -1)
                    {
                        s.FullText = s.FullText.Remove(emoji.start, emoji.end - emoji.start);
                        s.CursorIndex = s.SelectionStart = s.SelectionEnd = emoji.start;
                    }
                    else
                    {
                        int segStart = GetPrevSegmentStart(s.FullText, s.CursorIndex);
                        s.FullText = s.FullText.Remove(segStart, s.CursorIndex - segStart);
                        s.CursorIndex = s.SelectionStart = s.SelectionEnd = segStart;
                    }
                }
                else
                {
                    (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, true);
                    if (emoji.start != -1)
                    {
                        s.FullText = s.FullText.Remove(emoji.start, emoji.end - emoji.start);
                        s.CursorIndex = s.SelectionStart = s.SelectionEnd = emoji.start;
                    }
                    else
                    {
                        s.FullText = s.FullText.Remove(--s.CursorIndex, 1);
                        s.SelectionStart = s.SelectionEnd = s.CursorIndex;
                    }
                }

                RebuildText(__instance, s);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ChatBox), "receiveLeftClick")]
    public class ReceiveLeftClickPatch
    {
        private static bool Prefix(ChatBox __instance, int x, int y)
        {
            if (!__instance.chatBox.Selected) return false;

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
                TextBoxState s = GetState(__instance.chatBox);
                s.FullText = ChatMessage.makeMessagePlaintext(__instance.chatBox.finalText, false);
                s.CursorIndex = s.SelectionStart = s.SelectionEnd = s.FullText.Length;
                return false;
            }

            __instance.chatBox.Update();
            if (__instance.choosingEmoji)
            {
                __instance.choosingEmoji = false;
                __instance.emojiMenuIcon.scale = 4f;
            }

            if (__instance.isWithinBounds(x, y))
                __instance.chatBox.Selected = true;

            // Handle cursor positioning with emoji support
            if (!__instance.chatBox.Selected || !__instance.isWithinBounds(x, y) ||
                __instance.emojiMenuIcon.containsPoint(x, y) ||
                (__instance.choosingEmoji && __instance.emojiMenu.isWithinBounds(x, y))) return false;
            {
                TextBoxState s = GetState(__instance.chatBox);

                // Only handle initial click if not already dragging
                if (s.IsDragging) return false;
                int newCursor = CalculateCursorFromClick(__instance, x, s);

                bool shift = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                             Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);

                if (!shift)
                {
                    // Starting a new selection or placing cursor
                    s.CursorIndex = newCursor;
                    s.SelectionStart = newCursor;
                    s.IsDragging = true; // Start drag
                }

                s.SelectionEnd = newCursor;
                // If already dragging, let UpdatePatch handle the selection
            }
            return false;
        }

        private static int CalculateCursorFromClick(ChatBox chatBox, int x, TextBoxState s)
        {
            float clickX = x - chatBox.chatBox.X - 16 + s.ScrollOffset;

            ChatMessage msg = new();
            msg.parseMessageForEmoji(s.FullText);

            float curX = 0;
            int charCount = 0, newCursor = 0;
            SpriteFont? font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);

            foreach (ChatSnippet? snippet in msg.message)
                if (snippet.emojiIndex != -1)
                {
                    if (curX + snippet.myLength / 2 > clickX)
                    {
                        newCursor = charCount;
                        break;
                    }

                    curX += snippet.myLength;
                    charCount += snippet.emojiIndex.ToString().Length + 2;
                    newCursor = charCount;
                }
                else if (snippet.message != null)
                {
                    if (curX + snippet.myLength <= clickX)
                    {
                        curX += snippet.myLength;
                        charCount += snippet.message.Length;
                        newCursor = charCount;
                    }
                    else
                    {
                        for (int i = 0; i < snippet.message.Length; i++)
                        {
                            float charEndX = curX + font.MeasureString(snippet.message[..(i + 1)]).X;
                            float charStartX = i > 0 ? curX + font.MeasureString(snippet.message[..i]).X : curX;
                            float charMidX = (charStartX + charEndX) / 2;

                            if (!(clickX < charMidX)) continue;
                            newCursor = charCount + i;
                            goto Found;
                        }

                        newCursor = charCount + snippet.message.Length;
                        goto Found;
                    }
                }

            Found:
            return SnapCursorToEmojiBoundary(s.FullText, newCursor, 0);
        }
    }

    [HarmonyPatch(typeof(ChatBox), "receiveKeyPress")]
    public class ReceiveKeyPressPatch
    {
        private static bool Prefix(ChatBox __instance, Keys key)
        {
            if (!__instance.chatBox.Selected || !ModEntry.Instance.Config.EnableCursorControl)
                return true;

            TextBoxState s = GetState(__instance.chatBox);
            KeyboardState keys = Game1.input.GetKeyboardState();
            bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
            bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
            double time = Game1.currentGameTime.TotalGameTime.TotalSeconds;

            // Handle shortcuts
            if (ctrl)
                switch (key)
                {
                    case Keys.A:
                        s.SelectionStart = 0;
                        s.SelectionEnd = s.CursorIndex = s.FullText.Length;
                        return false;
                    case Keys.C when s.SelectionStart != s.SelectionEnd:
                        SetClipboard(s.FullText.Substring(
                            Math.Min(s.SelectionStart, s.SelectionEnd),
                            Math.Abs(s.SelectionEnd - s.SelectionStart)));
                        return false;
                    case Keys.X when s.SelectionStart != s.SelectionEnd:
                        SetClipboard(s.FullText.Substring(
                            Math.Min(s.SelectionStart, s.SelectionEnd),
                            Math.Abs(s.SelectionEnd - s.SelectionStart)));
                        DeleteSelection(__instance.chatBox);
                        return false;
                    case Keys.V:
                        InsertText(__instance.chatBox, GetClipboard());
                        return false;
                }

            // Handle cursor movement
            int newCursor = s.CursorIndex;
            bool handled = false;

            switch (key)
            {
                case Keys.Left:
                    newCursor = ctrl ? GetPrevSegmentStart(s.FullText, s.CursorIndex) :
                        s.CursorIndex > 0 ? s.CursorIndex - 1 : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, -1);
                    s.LastLeftPress = time;
                    handled = true;
                    break;
                case Keys.Right:
                    newCursor = ctrl ? GetNextSegmentEnd(s.FullText, s.CursorIndex) :
                        s.CursorIndex < s.FullText.Length ? s.CursorIndex + 1 : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, 1);
                    s.LastRightPress = time;
                    handled = true;
                    break;
                case Keys.Home:
                    newCursor = 0;
                    s.LastHomePress = time;
                    handled = true;
                    break;
                case Keys.End:
                    newCursor = s.FullText.Length;
                    s.LastEndPress = time;
                    handled = true;
                    break;
                case Keys.Delete:
                    s.ScrollOffset = 0;
                    if (s.SelectionStart != s.SelectionEnd)
                    {
                        DeleteSelection(__instance.chatBox);
                    }
                    else if (s.CursorIndex < s.FullText.Length)
                    {
                        if (ctrl)
                        {
                            (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, false);
                            int segEnd = GetNextSegmentEnd(s.FullText, s.CursorIndex);
                            s.FullText = emoji.start != -1
                                ? s.FullText.Remove(emoji.start, emoji.end - emoji.start)
                                : s.FullText = s.FullText.Remove(s.CursorIndex, segEnd - s.CursorIndex);
                        }
                        else
                        {
                            (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, false);
                            s.FullText = emoji.start != -1
                                ? s.FullText.Remove(emoji.start, emoji.end - emoji.start)
                                : s.FullText.Remove(s.CursorIndex, 1);
                        }

                        RebuildText(__instance.chatBox, s);
                    }

                    return false;
            }

            if (!handled) return true;
            UpdateSelection(s, newCursor, shift);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatBox), "textBoxEnter", typeof(TextBox))]
    public class TextBoxEnterPatch
    {
        private static bool Prefix(ChatBox __instance, TextBox sender)
        {
            if (sender is not ChatTextBox box) return true;
            TextBoxState s = GetState(box);

            if (s.FullText.Length <= ModEntry.Instance.Config.MaxMessageLength) return true;
            __instance.addErrorMessage(
                $"Message too long! Maximum {ModEntry.Instance.Config.MaxMessageLength} characters.");
            return false;
        }

        private static void Postfix(TextBox sender)
        {
            if (sender is not ChatTextBox box) return;
            TextBoxState s = GetState(box);

            s.FullText = "";
            s.CursorIndex = s.SelectionStart = s.SelectionEnd = 0;
            s.ScrollOffset = 0;

            box.finalText.Clear();
            box.finalText.Add(new ChatSnippet("", LocalizedContentManager.CurrentLanguageCode));
            box.updateWidth();
        }
    }

    [HarmonyPatch(typeof(ChatTextBox), "Draw")]
    public class DrawPatch
    {
        private static readonly AccessTools.FieldRef<ChatTextBox, Texture2D> _textBoxTexture =
            AccessTools.FieldRefAccess<ChatTextBox, Texture2D>("_textBoxTexture");

        private static readonly AccessTools.FieldRef<ChatTextBox, Color> _textColor =
            AccessTools.FieldRefAccess<ChatTextBox, Color>("_textColor");

        private static bool Prefix(ChatTextBox __instance, SpriteBatch spriteBatch, bool drawShadow)
        {
            if (!ModEntry.Instance.Config.EnableHorizontalScrolling) return true;

            Texture2D? tex = _textBoxTexture(__instance);
            Color color = _textColor(__instance);
            bool showCursor = Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 >= 500.0;

            // Draw background
            if (tex != null)
            {
                spriteBatch.Draw(tex, new Rectangle(__instance.X, __instance.Y, 16, __instance.Height),
                    new Rectangle(0, 0, 16, __instance.Height), Color.White);
                spriteBatch.Draw(tex,
                    new Rectangle(__instance.X + 16, __instance.Y, __instance.Width - 32, __instance.Height),
                    new Rectangle(16, 0, 4, __instance.Height), Color.White);
                spriteBatch.Draw(tex,
                    new Rectangle(__instance.X + __instance.Width - 16, __instance.Y, 16, __instance.Height),
                    new Rectangle(tex.Bounds.Width - 16, 0, 16, __instance.Height), Color.White);
            }
            else
            {
                Game1.drawDialogueBox(__instance.X - 32, __instance.Y - 112 + 10, __instance.Width + 80,
                    __instance.Height, false, true);
            }

            TextBoxState s = GetState(__instance);
            SpriteFont? font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);
            float maxWidth = __instance.Width - 72f;

            // Parse message for rendering
            ChatMessage msg = new();
            msg.parseMessageForEmoji(s.FullText);
            msg.color = ChatMessage.getColorFromName(Game1.player.defaultChatColor);
            msg.language = LocalizedContentManager.CurrentLanguageCode;

            float totalWidth = msg.message.Sum(snippet => snippet.myLength);

            // Calculate cursor position
            float cursorPixel = 0f;
            if (s.CursorIndex > 0)
            {
                int charCount = 0;
                foreach (ChatSnippet? snippet in msg.message)
                    if (snippet.emojiIndex != -1)
                    {
                        int emojiChars = snippet.emojiIndex.ToString().Length + 2;
                        if (charCount + emojiChars <= s.CursorIndex)
                        {
                            cursorPixel += snippet.myLength;
                            charCount += emojiChars;
                        }
                        else if (charCount < s.CursorIndex)
                        {
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
                        int len = snippet.message.Length;
                        if (charCount + len <= s.CursorIndex)
                        {
                            cursorPixel += snippet.myLength;
                            charCount += len;
                        }
                        else if (charCount < s.CursorIndex)
                        {
                            cursorPixel += font.MeasureString(snippet.message[..(s.CursorIndex - charCount)]).X;
                            break;
                        }
                        else
                        {
                            break;
                        }
                    }
            }

            // Update scroll
            float cursorView = cursorPixel - s.ScrollOffset;
            if (cursorView > maxWidth) s.ScrollOffset = cursorPixel - maxWidth;
            else if (cursorView < 0) s.ScrollOffset = Math.Max(0, cursorPixel);
            s.ScrollOffset = Math.Clamp(s.ScrollOffset, 0, Math.Max(0, totalWidth - maxWidth));

            // Set up clipping
            Rectangle oldScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            RasterizerState? oldRaster = spriteBatch.GraphicsDevice.RasterizerState;
            spriteBatch.End();

            RasterizerState raster = new() { ScissorTestEnable = true };
            Rectangle scissor = new(__instance.X + 16,
                __instance.Y,
                __instance.Width - 72,
                __instance.Height);
            spriteBatch.GraphicsDevice.ScissorRectangle = scissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, raster);

            // Draw selection
            if (s.SelectionStart != s.SelectionEnd)
            {
                int min = Math.Min(s.SelectionStart, s.SelectionEnd);
                int max = Math.Max(s.SelectionStart, s.SelectionEnd);
                float selStart = min > 0 ? font.MeasureString(s.FullText[..min]).X : 0f;
                float selEnd = max > 0 ? font.MeasureString(s.FullText[..max]).X : 0f;
                float textX = __instance.X + 16f - s.ScrollOffset;
                spriteBatch.Draw(Game1.staminaRect,
                    new Rectangle((int)(textX + selStart), __instance.Y + 8, (int)(selEnd - selStart), 32),
                    new Color(0, 120, 215, 100));
            }

            // Draw text and emojis
            float x = __instance.X + 16f - s.ScrollOffset;
            float y = __instance.Y + 12;
            foreach (ChatSnippet? snippet in msg.message)
            {
                if (snippet.emojiIndex != -1)
                    spriteBatch.Draw(ChatBox.emojiTexture, new Vector2(x + 1f, y - 4f),
                        new Rectangle(snippet.emojiIndex * 9 % ChatBox.emojiTexture.Width,
                            snippet.emojiIndex * 9 / ChatBox.emojiTexture.Width * 9, 9, 9),
                        Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.99f);
                else if (snippet.message != null)
                    spriteBatch.DrawString(font, snippet.message, new Vector2(x, y), msg.color,
                        0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                x += snippet.myLength;
            }

            // Draw cursor
            if (showCursor && __instance.Selected)
            {
                float cursorX = __instance.X + 16f + cursorPixel - s.ScrollOffset;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)(cursorX - 1), __instance.Y + 8, 2, 32), color);
            }

            // Restore state
            spriteBatch.End();
            spriteBatch.GraphicsDevice.ScissorRectangle = oldScissor;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, oldRaster);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatBox), "update", typeof(GameTime))]
    public class UpdatePatch
    {
        private static void Postfix(ChatBox __instance, GameTime time)
        {
            if (!__instance.chatBox.Selected) return;

            TextBoxState s = GetState(__instance.chatBox);
            MouseState mouseState = Game1.input.GetMouseState();
            bool isMousePressed = mouseState.LeftButton == ButtonState.Pressed;

            // Handle drag selection
            if (s.IsDragging && isMousePressed)
            {
                Point mouse = Game1.getMousePosition();
                int mouseX = (int)(mouse.X / Game1.options.zoomLevel);

                // Check if mouse is over the text box area
                if (mouseX >= __instance.chatBox.X && mouseX <= __instance.chatBox.X + __instance.chatBox.Width - 72)
                {
                    int newCursor = CalculateCursorFromClick(__instance, mouseX, s);
                    s.SelectionEnd = newCursor;
                    s.CursorIndex = newCursor;
                }
            }

            // Detect mouse release (transition from pressed to not pressed)
            if (s.WasMousePressed && !isMousePressed) s.IsDragging = false;

            s.WasMousePressed = isMousePressed;

            // Rest of your existing key repeat code
            if (!ModEntry.Instance.Config.EnableCursorControl) return;

            double now = time.TotalGameTime.TotalSeconds;
            KeyboardState keys = Game1.input.GetKeyboardState();
            bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
            bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);

            float initDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
            float repDelay = ModEntry.Instance.Config.KeyRepeatDelay;

            HandleKeyRepeat(Keys.Left, keys, s, now, initDelay, repDelay, shift, ctrl,
                ref s.LastLeftPress, ref s.LastLeftRepeat,
                () => ctrl ? GetPrevSegmentStart(s.FullText, s.CursorIndex) :
                    s.CursorIndex > 0 ? s.CursorIndex - 1 : s.CursorIndex,
                -1);

            HandleKeyRepeat(Keys.Right, keys, s, now, initDelay, repDelay, shift, ctrl,
                ref s.LastRightPress, ref s.LastRightRepeat,
                () => ctrl ? GetNextSegmentEnd(s.FullText, s.CursorIndex) :
                    s.CursorIndex < s.FullText.Length ? s.CursorIndex + 1 : s.CursorIndex,
                1);

            HandleKeyRepeat(Keys.Home, keys, s, now, initDelay, repDelay, shift, ctrl,
                ref s.LastHomePress, ref s.LastHomeRepeat, () => 0, 0);

            HandleKeyRepeat(Keys.End, keys, s, now, initDelay, repDelay, shift, ctrl,
                ref s.LastEndPress, ref s.LastEndRepeat, () => s.FullText.Length, 0);
        }

        private static int CalculateCursorFromClick(ChatBox chatBox, int x, TextBoxState s)
        {
            float clickX = x - chatBox.chatBox.X - 16 + s.ScrollOffset;

            ChatMessage msg = new();
            msg.parseMessageForEmoji(s.FullText);

            float curX = 0;
            int charCount = 0, newCursor = 0;
            SpriteFont? font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);

            foreach (ChatSnippet? snippet in msg.message)
                if (snippet.emojiIndex != -1)
                {
                    if (curX + snippet.myLength / 2 > clickX)
                    {
                        newCursor = charCount;
                        break;
                    }

                    curX += snippet.myLength;
                    charCount += snippet.emojiIndex.ToString().Length + 2;
                    newCursor = charCount;
                }
                else if (snippet.message != null)
                {
                    if (curX + snippet.myLength <= clickX)
                    {
                        curX += snippet.myLength;
                        charCount += snippet.message.Length;
                        newCursor = charCount;
                    }
                    else
                    {
                        for (int i = 0; i < snippet.message.Length; i++)
                        {
                            float charEndX = curX + font.MeasureString(snippet.message[..(i + 1)]).X;
                            float charStartX = i > 0 ? curX + font.MeasureString(snippet.message[..i]).X : curX;
                            float charMidX = (charStartX + charEndX) / 2;

                            if (!(clickX < charMidX)) continue;
                            newCursor = charCount + i;
                            goto Found;
                        }

                        newCursor = charCount + snippet.message.Length;
                        goto Found;
                    }
                }

            Found:
            return SnapCursorToEmojiBoundary(s.FullText, newCursor, 0);
        }

        private static void HandleKeyRepeat(Keys key, KeyboardState keys, TextBoxState s,
            double now, float initDelay, float repDelay, bool shift, bool ctrl,
            ref double lastPress, ref double lastRepeat, Func<int> getNewPos, int emojiDir)
        {
            if (keys.IsKeyDown(key))
            {
                if (!(now - lastPress >= initDelay) || !(now - lastRepeat >= repDelay)) return;
                int newCursor = getNewPos();
                if (emojiDir != 0)
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, emojiDir);

                if (newCursor == s.CursorIndex) return;
                UpdateSelection(s, newCursor, shift);
                lastRepeat = now;
            }
            else
            {
                lastPress = lastRepeat = 0;
            }
        }
    }

    #endregion
}
#pragma warning restore CS8602