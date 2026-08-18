using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

/// <summary>
/// Harmony patches for ChatTextBox that implement enhanced text input features:
/// - Cursor navigation (arrow keys, Home/End, Ctrl+Arrow for word jumping)
/// - Text selection with Shift+Arrow keys
/// - Clipboard operations (Copy/Cut/Paste) with proper OS integration
/// - Undo/Redo with smart snapshotting
/// - Horizontal scrolling for long messages
/// - Emoji boundary snapping
/// </summary>
internal class ChatTextBoxPatches
{
    #region Constants

    private const int MaxUndoHistorySize = 500;
    private const double IdleSnapshotDelay = 1.5;
    private const int SnapshotCharacterInterval = 10;
    private const float TextBoxPadding = 16f;
    private const float TextBoxWidthPadding = 72f;

    #endregion

    #region State Management

    private static readonly Dictionary<ChatTextBox, TextBoxState> States = new();

    private static TextBoxState GetState(ChatTextBox box)
    {
        if (States.TryGetValue(box, out TextBoxState? state))
            return state;

        state = new TextBoxState();
        States[box] = state;
        return state;
    }

    #endregion

    #region Smart Snapshot Logic

    private static void MaybeSnapshot(TextBoxState s, OperationType currentOp, bool force = false)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;

        if (force)
        {
            TakeSnapshot(s);
            s.LastOperation = currentOp;
            s.CharsSinceSnapshot = 0;
            s.LastSnapshotCursor = s.CursorIndex;
            s.LastSnapshotTime = now;
            return;
        }

        // Operation changed (e.g. typing -> backspace)
        if (s.LastOperation != OperationType.None && s.LastOperation != currentOp)
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
        }

        // Cursor jumped manually
        if (currentOp == OperationType.Typing && s.LastSnapshotCursor >= 0)
        {
            if (s.CursorIndex != s.LastSnapshotCursor &&
                s.CursorIndex != s.LastSnapshotCursor + s.CharsSinceSnapshot)
            {
                TakeSnapshot(s);
                s.CharsSinceSnapshot = 0;
            }
        }

        // Interval check
        if (currentOp == OperationType.Typing && s.CharsSinceSnapshot >= SnapshotCharacterInterval)
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
        }

        s.LastOperation = currentOp;
        s.LastSnapshotTime = now;
        s.LastSnapshotCursor = s.CursorIndex;
    }

    private static void CheckIdleSnapshot(TextBoxState s)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;

        if (s.LastOperation != OperationType.None &&
            now - s.LastSnapshotTime >= IdleSnapshotDelay &&
            s.CharsSinceSnapshot > 0)
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
            s.LastOperation = OperationType.None;
        }
    }

    private static void TakeSnapshot(TextBoxState s)
    {
        // Dedup
        if (s.UndoStack.Count > 0)
        {
            HistoryState last = s.UndoStack.Peek();
            if (last.Text == s.FullText && last.Cursor == s.CursorIndex &&
                last.SelectionStart == s.SelectionStart && last.SelectionEnd == s.SelectionEnd)
                return;
        }

        s.UndoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));

        if (s.UndoStack.Count > MaxUndoHistorySize)
        {
            // Truncate oldest by rebuilding stack (inefficient but rare)
            List<HistoryState> items = s.UndoStack.ToList();
            items.RemoveAt(items.Count - 1);
            s.UndoStack.Clear();
            // ToList returns items in Stack order (Newest first), so reversing gives Oldest first.
            // Push expects items.
            // Start from bottom: items[Last] is oldest.
            for (int i = items.Count - 1; i >= 0; i--)
                s.UndoStack.Push(items[i]);
        }

        s.RedoStack.Clear();
    }

    private static void RestoreState(ChatTextBox box, TextBoxState s, HistoryState state)
    {
        s.FullText = state.Text;
        s.CursorIndex = state.Cursor;
        s.SelectionStart = state.SelectionStart;
        s.SelectionEnd = state.SelectionEnd;
        s.CharsSinceSnapshot = 0;
        s.LastOperation = OperationType.None;
        s.LastSnapshotCursor = s.CursorIndex;
        RebuildText(box, s);
    }

    #endregion

    #region Text Navigation & Editing

    /// <summary>
    /// Calculates cursor position from a mouse click X coordinate.
    /// </summary>
    private static int CalculateCursorFromClick(ChatBox chatBox, int x, TextBoxState s)
    {
        float clickX = x - chatBox.chatBox.X - TextBoxPadding + s.ScrollOffset;
        // language must be set BEFORE parsing: ChatSnippet measures itself with
        // messageFont(language), and the default (en) has different metrics to the
        // font this text is actually drawn with in ja/ko/zh/ru.
        ChatMessage msg = new() { language = LocalizedContentManager.CurrentLanguageCode };
        msg.parseMessageForEmoji(s.FullText);
        SpriteFont? font = ChatBox.messageFont(LocalizedContentManager.CurrentLanguageCode);
        if (font == null) return s.FullText.Length;

        float currentX = 0f;
        int charCount = 0;

        foreach (ChatSnippet snippet in msg.message)
        {
            if (snippet.emojiIndex != -1)
            {
                // Emoji (approx width check)
                if (currentX + snippet.myLength / 2 > clickX)
                    return EmojiHelper.SnapToBoundary(s.FullText, charCount, 0);

                currentX += snippet.myLength;
                charCount += snippet.emojiIndex.ToString(CultureInfo.InvariantCulture).Length + 2; // [123]
            }
            else if (snippet.message != null)
            {
                // Text
                if (currentX + snippet.myLength <= clickX)
                {
                    currentX += snippet.myLength;
                    charCount += snippet.message.Length;
                }
                else
                {
                    // Click inside text snippet - use binary search to reduce MeasureString calls
                    float target = clickX - currentX;
                    int idx = FindClosestCharIndex(snippet.message, target, font);
                    return EmojiHelper.SnapToBoundary(s.FullText, charCount + idx, 0);
                }
            }
        }

        return s.FullText.Length;
    }

    private static int FindClosestCharIndex(string text, float targetWidth, SpriteFont font)
    {
        // Binary search for smallest i where width(text[..i]) >= targetWidth
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            float w = mid == 0 ? 0f : font.MeasureString(text.Substring(0, mid)).X;
            if (w < targetWidth) lo = mid + 1; else hi = mid;
        }
        int i = Math.Clamp(lo, 0, text.Length);
        // Choose closer between i and i-1
        float wi = i == 0 ? 0f : font.MeasureString(text.Substring(0, i)).X;
        float wim1 = i <= 1 ? 0f : font.MeasureString(text.Substring(0, i - 1)).X;
        return (wi - targetWidth) <= (targetWidth - wim1) ? i : i - 1;
    }

    private static void InsertText(ChatTextBox box, string text)
    {
        TextBoxState s = GetState(box);
        MaybeSnapshot(s, OperationType.Typing);

        // Pre-snap insertion point to avoid inserting inside finalized emoji tokens
        int snappedBefore = EmojiHelper.SnapToBoundary(s.FullText, s.CursorIndex, 1);
        if (snappedBefore != s.CursorIndex)
        {
            s.CursorIndex = snappedBefore;
            s.SelectionStart = s.SelectionEnd = s.CursorIndex;
        }

        // Delete selection
        if (s.SelectionStart != s.SelectionEnd)
        {
            int start = Math.Min(s.SelectionStart, s.SelectionEnd);
            int length = Math.Abs(s.SelectionEnd - s.SelectionStart);
            s.FullText = s.FullText.Remove(start, length);
            s.CursorIndex = start;
            s.SelectionStart = s.SelectionEnd = 0;
        }

        if (s.FullText.Length + text.Length > (ModEntry.Instance?.Config.MaxMessageLength ?? 100))
            return;

        // Clamp cursor to valid bounds to prevent AOOR during insert
        s.CursorIndex = Math.Clamp(s.CursorIndex, 0, s.FullText.Length);
        s.FullText = s.FullText.Insert(s.CursorIndex, text);
        s.CursorIndex += text.Length;
        // Post-snap to ensure cursor is not left inside an emoji token after edits (e.g., inserting '[' before a closing "]")
        s.CursorIndex = EmojiHelper.SnapToBoundary(s.FullText, s.CursorIndex, 1);
        s.SelectionStart = s.SelectionEnd = s.CursorIndex;
        s.CharsSinceSnapshot += text.Length;
        s.LastTypingTime = Game1.currentGameTime.TotalGameTime.TotalSeconds;

        RebuildText(box, s);
    }

    private static void DeleteSelection(ChatTextBox box)
    {
        TextBoxState s = GetState(box);
        if (s.SelectionStart == s.SelectionEnd) return;

        MaybeSnapshot(s, OperationType.Delete, true);

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
        if (shift)
        {
            if (s.SelectionStart == s.SelectionEnd)
                s.SelectionStart = s.CursorIndex;
            s.SelectionEnd = newCursor;
        }
        else
        {
            s.SelectionStart = s.SelectionEnd = newCursor;
        }
        s.CursorIndex = newCursor;
    }

    #region Word Navigation Helpers

    private static int GetNextSegmentEnd(string text, int pos)
    {
        if (pos >= text.Length) return pos;
        if (char.IsWhiteSpace(text[pos]))
            return AdvanceWhile(text, pos, char.IsWhiteSpace);
        return char.IsLetterOrDigit(text[pos])
            ? AdvanceWhile(text, pos, char.IsLetterOrDigit)
            : AdvanceWhile(text, pos, ch => !char.IsWhiteSpace(ch) && !char.IsLetterOrDigit(ch));
    }

    private static int GetPrevSegmentStart(string text, int pos)
    {
        if (pos <= 0) return 0;
        if (char.IsWhiteSpace(text[pos - 1]))
            return RetreatWhile(text, pos - 1, char.IsWhiteSpace);
        return char.IsLetterOrDigit(text[pos - 1])
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

    #endregion

    #region Harmony Patches

    [HarmonyPatch(typeof(ChatTextBox), "RecieveTextInput", typeof(string))]
    public class ReceiveTextInputStringPatch
    {
        private static bool Prefix(ChatTextBox __instance, string text)
        {
            if (!__instance.Selected) return true;

            InsertText(__instance, text);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChatTextBox), "receiveEmoji")]
    public class ReceiveEmojiPatch
    {
        private static bool Prefix(ChatTextBox __instance, int emoji)
        {
            if (!(ModEntry.Instance?.Config.EnableHorizontalScrolling ?? false)) return true;

            TextBoxState s = GetState(__instance); // Fixed: was __instance which is ChatTextBox

            // Emoji codes ~10 chars
            if (s.FullText.Length + 10 > (ModEntry.Instance?.Config.MaxMessageLength ?? 100))
                return false;

            MaybeSnapshot(s, OperationType.Emoji, true);

            __instance.finalText.Add(new ChatSnippet(emoji));
            __instance.updateWidth();

            s.FullText = ChatMessage.makeMessagePlaintext(__instance.finalText, false);
            s.CursorIndex = s.SelectionStart = s.SelectionEnd = s.FullText.Length;

            return false;
        }
    }

    [HarmonyPatch(typeof(ChatTextBox), "RecieveCommandInput", typeof(char))]
    public class ReceiveCommandInputPatch
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
                return false;
            }

            if (s.CursorIndex > 0)
            {
                MaybeSnapshot(s, OperationType.Backspace);

                if (ctrl)
                {
                    var emoji = EmojiHelper.GetEmojiRange(s.FullText, s.CursorIndex, true);
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
                    var emoji = EmojiHelper.GetEmojiRange(s.FullText, s.CursorIndex, true);
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
                s.CharsSinceSnapshot++;
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

            if (!__instance.chatBox.Selected || !__instance.isWithinBounds(x, y) ||
                __instance.emojiMenuIcon.containsPoint(x, y) ||
                (__instance.choosingEmoji && __instance.emojiMenu.isWithinBounds(x, y)))
            {
                return false;
            }

            TextBoxState state = GetState(__instance.chatBox);
            if (state.IsDragging) return false;

            int newCursor = CalculateCursorFromClick(__instance, x, state);
            bool shift = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                         Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);

            if (!shift)
            {
                state.CursorIndex = newCursor;
                state.SelectionStart = newCursor;
                state.IsDragging = true;
            }
            state.SelectionEnd = newCursor;

            return false;
        }
    }

    [HarmonyPatch(typeof(ChatBox), "receiveKeyPress")]
    public class ReceiveKeyPressPatch
    {
        private static bool Prefix(ChatBox __instance, Keys key)
        {
            if (!__instance.chatBox.Selected || !(ModEntry.Instance?.Config.EnableCursorControl ?? false))
                return true;

            TextBoxState s = GetState(__instance.chatBox);
            bool shift = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                         Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);
            bool ctrl = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftControl) ||
                        Game1.input.GetKeyboardState().IsKeyDown(Keys.RightControl);

            // Undo
            if (ModEntry.Instance.Config.UndoKeybind.JustPressed())
            {
                if (s.UndoStack.TryPop(out HistoryState undoState))
                {
                    s.RedoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                    RestoreState(__instance.chatBox, s, undoState);
                }
                return false;
            }

            // Redo
            if (ModEntry.Instance.Config.RedoKeybind.JustPressed())
            {
                if (s.RedoStack.TryPop(out HistoryState redoState))
                {
                    s.UndoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                    RestoreState(__instance.chatBox, s, redoState);
                }
                return false;
            }

            // Copy
            if (ModEntry.Instance.Config.CopyKeybind.JustPressed())
            {
                if (s.SelectionStart != s.SelectionEnd)
                {
                    ClipboardHelper.SetText(s.FullText.Substring(
                        Math.Min(s.SelectionStart, s.SelectionEnd),
                        Math.Abs(s.SelectionEnd - s.SelectionStart)));
                }
                return false;
            }

            // Cut
            if (ModEntry.Instance.Config.CutKeybind.JustPressed())
            {
                if (s.SelectionStart != s.SelectionEnd)
                {
                    ClipboardHelper.SetText(s.FullText.Substring(
                        Math.Min(s.SelectionStart, s.SelectionEnd),
                        Math.Abs(s.SelectionEnd - s.SelectionStart)));
                    DeleteSelection(__instance.chatBox);
                }
                return false;
            }

            // Paste
            if (ModEntry.Instance.Config.PasteKeybind.JustPressed())
            {
                MaybeSnapshot(s, OperationType.Paste, true);
            string paste = ClipboardHelper.GetText()?.TrimEnd('\r', '\n') ?? "";
            if (!string.IsNullOrEmpty(paste)) //Need to test this on windows to see if our 'ghost-pasting' is present (paste being handled by something else)
                InsertText(__instance.chatBox, paste);
            return false;
            }

            // Select All
            if (ModEntry.Instance.Config.SelectAllKeybind.JustPressed())
            {
                s.SelectionStart = 0;
                s.SelectionEnd = s.CursorIndex = s.FullText.Length;
                return false;
            }

            // Navigation
            bool handled = false;
            int newCursor = s.CursorIndex;

            switch (key)
            {
                case Keys.Left:
                    newCursor = ctrl ? GetPrevSegmentStart(s.FullText, s.CursorIndex) : Math.Max(0, s.CursorIndex - 1);
                    // If we would land inside an emoji, snap to the start so we never end up between brackets
                    int snappedLeft = EmojiHelper.SnapToBoundary(s.FullText, newCursor, -1);
                    if (snappedLeft != newCursor)
                        newCursor = snappedLeft;
                    handled = true;
                    if (s.LastOperation == OperationType.Typing) s.LastOperation = OperationType.CursorMove;
                    s.LastLeftPress = Game1.currentGameTime.TotalGameTime.TotalSeconds;
                    break;
                case Keys.Right:
                    newCursor = ctrl ? GetNextSegmentEnd(s.FullText, s.CursorIndex) : Math.Min(s.FullText.Length, s.CursorIndex + 1);
                    // If we would land inside an emoji, snap to the end so we never end up between brackets
                    int snappedRight = EmojiHelper.SnapToBoundary(s.FullText, newCursor, 1);
                    if (snappedRight != newCursor)
                        newCursor = snappedRight;
                    handled = true;
                    if (s.LastOperation == OperationType.Typing) s.LastOperation = OperationType.CursorMove;
                    s.LastRightPress = Game1.currentGameTime.TotalGameTime.TotalSeconds;
                    break;
                case Keys.Home:
                    newCursor = 0;
                    handled = true;
                    if (s.LastOperation == OperationType.Typing) s.LastOperation = OperationType.CursorMove;
                    s.LastHomePress = Game1.currentGameTime.TotalGameTime.TotalSeconds;
                    break;
                case Keys.End:
                    newCursor = s.FullText.Length;
                    handled = true;
                    if (s.LastOperation == OperationType.Typing) s.LastOperation = OperationType.CursorMove;
                    s.LastEndPress = Game1.currentGameTime.TotalGameTime.TotalSeconds;
                    break;
                case Keys.Delete:
                    s.ScrollOffset = 0;
                    if (s.SelectionStart != s.SelectionEnd)
                    {
                        DeleteSelection(__instance.chatBox);
                    }
                    else if (s.CursorIndex < s.FullText.Length)
                    {
                        MaybeSnapshot(s, OperationType.Delete);
                        if (ctrl)
                        {
                            var emoji = EmojiHelper.GetEmojiRange(s.FullText, s.CursorIndex, false);
                            if (emoji.start != -1)
                            {
                                s.FullText = s.FullText.Remove(emoji.start, emoji.end - emoji.start);
                                s.CursorIndex = s.SelectionStart = s.SelectionEnd = Math.Clamp(emoji.start, 0, s.FullText.Length);
                            }
                            else
                            {
                                int segEnd = GetNextSegmentEnd(s.FullText, s.CursorIndex);
                                s.FullText = s.FullText.Remove(s.CursorIndex, segEnd - s.CursorIndex);
                                s.CursorIndex = s.SelectionStart = s.SelectionEnd = Math.Clamp(s.CursorIndex, 0, s.FullText.Length);
                            }
                        }
                        else
                        {
                            var emoji = EmojiHelper.GetEmojiRange(s.FullText, s.CursorIndex, false);
                            if (emoji.start != -1)
                            {
                                s.FullText = s.FullText.Remove(emoji.start, emoji.end - emoji.start);
                                s.CursorIndex = s.SelectionStart = s.SelectionEnd = Math.Clamp(emoji.start, 0, s.FullText.Length);
                            }
                            else
                            {
                                s.FullText = s.FullText.Remove(s.CursorIndex, 1);
                                s.CursorIndex = s.SelectionStart = s.SelectionEnd = Math.Clamp(s.CursorIndex, 0, s.FullText.Length);
                            }
                        }
                        s.CharsSinceSnapshot++;
                        RebuildText(__instance.chatBox, s);
                    }
                    return false;
            }

            if (handled)
            {
                UpdateSelection(s, newCursor, shift);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ChatBox), "textBoxEnter", typeof(TextBox))]
    public class TextBoxEnterPatch
    {
        private static bool Prefix(ChatBox __instance, TextBox sender)
        {
            if (sender is not ChatTextBox box) return true;
            TextBoxState s = GetState(box);

            if (s.FullText.Length > (ModEntry.Instance?.Config.MaxMessageLength ?? 100))
            {
                var t = ModEntry.Instance?.Helper.Translation;
                __instance.addErrorMessage(
                    string.Format(CultureInfo.CurrentCulture, t?.Get("hud.messageTooLong") ?? "Message too long!",
                        ModEntry.Instance?.Config.MaxMessageLength ?? 0));
                return false;
            }
            return true;
        }

        private static void Postfix(TextBox sender)
        {
            if (sender is not ChatTextBox box) return;
            GetState(box).Reset();
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

        private static bool Prefix(ChatTextBox __instance, SpriteBatch spriteBatch)
        {
            if (!(ModEntry.Instance?.Config.EnableHorizontalScrolling ?? false)) return true;

            Texture2D? tex = _textBoxTexture(__instance);

            // Preview the message in the colour it will actually be sent in. The text box's own
            // _textColor is always white, so using it hides the player's chosen chat colour.
            Color textColor = ChatMessage.getColorFromName(Game1.player.defaultChatColor);
            Color cursorColor = _textColor(__instance);
            bool showCursor = Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 >= 500.0;

            DrawTextBoxBackground(spriteBatch, __instance, tex);

            TextBoxState s = GetState(__instance);
            var lang = LocalizedContentManager.CurrentLanguageCode;
            SpriteFont? font = ChatBox.messageFont(lang);
            if (font == null)
            {
                return true;
            }

            float visibleWidth = __instance.Width - TextBoxWidthPadding;

            // language must be set BEFORE parsing, so snippet widths are measured with the
            // same font the text is drawn with. Otherwise the caret drifts in ja/ko/zh/ru.
            ChatMessage msg = new() { language = lang };
            msg.parseMessageForEmoji(s.FullText);

            float cursorPixel = CalculateCursorPixelPosition(msg.message, s.CursorIndex, font);
            float totalWidth = 0f;
            foreach (var sn in msg.message)
                totalWidth += sn.myLength;

            UpdateScrollOffset(s, cursorPixel, visibleWidth, totalWidth);

            var (oldScissor, oldRaster) = BeginClippedRendering(spriteBatch, __instance);

            if (s.SelectionStart != s.SelectionEnd)
                DrawSelectionHighlight(spriteBatch, s, font, __instance);

            DrawTextContent(spriteBatch, msg.message, __instance, s, font, textColor);

            if (showCursor && __instance.Selected)
                DrawCursor(spriteBatch, __instance, cursorPixel, s.ScrollOffset, cursorColor);

            EndClippedRendering(spriteBatch, oldScissor, oldRaster);
            return false;
        }

        private static void DrawTextBoxBackground(SpriteBatch b, ChatTextBox box, Texture2D? tex)
        {
            if (tex != null)
            {
                b.Draw(tex, new Rectangle(box.X, box.Y, 16, box.Height), new Rectangle(0, 0, 16, box.Height), Color.White);
                b.Draw(tex, new Rectangle(box.X + 16, box.Y, box.Width - 32, box.Height), new Rectangle(16, 0, 4, box.Height), Color.White);
                b.Draw(tex, new Rectangle(box.X + box.Width - 16, box.Y, 16, box.Height), new Rectangle(tex.Bounds.Width - 16, 0, 16, box.Height), Color.White);
            }
            else
            {
                Game1.drawDialogueBox(box.X - 32, box.Y - 112 + 10, box.Width + 80, box.Height, false, true);
            }
        }

        private static float CalculateCursorPixelPosition(List<ChatSnippet> snippets, int cursorIndex, SpriteFont font)
        {
            if (cursorIndex == 0) return 0f;
            float px = 0f;
            int count = 0;

            foreach (var snippet in snippets)
            {
                if (snippet.emojiIndex != -1)
                {
                    int len = snippet.emojiIndex.ToString(CultureInfo.InvariantCulture).Length + 2;
                    if (count + len <= cursorIndex)
                    {
                        px += snippet.myLength;
                        count += len;
                    }
                    else break;
                }
                else if (snippet.message != null)
                {
                    int len = snippet.message.Length;
                    if (count + len <= cursorIndex)
                    {
                        px += snippet.myLength;
                        count += len;
                    }
                    else
                    {
                        px += font.MeasureString(snippet.message[..(cursorIndex - count)]).X;
                        break;
                    }
                }
            }
            return px;
        }

        private static void UpdateScrollOffset(TextBoxState s, float cursorPixel, float maxWidth, float totalWidth)
        {
            float cursorView = cursorPixel - s.ScrollOffset;
            if (cursorView > maxWidth) s.ScrollOffset = cursorPixel - maxWidth;
            else if (cursorView < 0) s.ScrollOffset = Math.Max(0, cursorPixel);
            s.ScrollOffset = Math.Clamp(s.ScrollOffset, 0, Math.Max(0, totalWidth - maxWidth));
        }

        private static (Rectangle, RasterizerState?) BeginClippedRendering(SpriteBatch b, ChatTextBox box)
        {
            Rectangle oldScissor = b.GraphicsDevice.ScissorRectangle;
            RasterizerState? oldRaster = b.GraphicsDevice.RasterizerState;
            b.End();

            b.GraphicsDevice.ScissorRectangle = new Rectangle(box.X + 16, box.Y, box.Width - 72, box.Height);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, new RasterizerState { ScissorTestEnable = true });

            return (oldScissor, oldRaster);
        }

        private static void EndClippedRendering(SpriteBatch b, Rectangle oldScissor, RasterizerState? oldRaster)
        {
            b.End();
            b.GraphicsDevice.ScissorRectangle = oldScissor;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, oldRaster);
        }

        private static void DrawSelectionHighlight(SpriteBatch b, TextBoxState s, SpriteFont font, ChatTextBox box)
        {
            int min = Math.Min(s.SelectionStart, s.SelectionEnd);
            int max = Math.Max(s.SelectionStart, s.SelectionEnd);

            float startX = min > 0 ? font.MeasureString(s.FullText[..min]).X : 0f;
            float endX = max > 0 ? font.MeasureString(s.FullText[..max]).X : 0f;

            b.Draw(Game1.staminaRect,
                new Rectangle((int)(box.X + TextBoxPadding - s.ScrollOffset + startX), box.Y + 8, (int)(endX - startX), 32),
                new Color(0, 120, 215, 100));
        }

        private static void DrawTextContent(SpriteBatch b, List<ChatSnippet> snippets, ChatTextBox box, TextBoxState s, SpriteFont font, Color color)
        {
            float x = box.X + TextBoxPadding - s.ScrollOffset;
            float y = box.Y + 12;

            foreach (var snippet in snippets)
            {
                if (snippet.emojiIndex != -1)
                {
                    b.Draw(ChatBox.emojiTexture, new Vector2(x + 1f, y - 4f),
                       new Rectangle(snippet.emojiIndex * 9 % ChatBox.emojiTexture.Width,
                           snippet.emojiIndex * 9 / ChatBox.emojiTexture.Width * 9, 9, 9),
                       Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.99f);
                }
                else if (snippet.message != null)
                {
                    b.DrawString(font, snippet.message, new Vector2(x, y), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
                }
                x += snippet.myLength;
            }
        }

        private static void DrawCursor(SpriteBatch b, ChatTextBox box, float pixel, float offset, Color color)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle((int)(box.X + TextBoxPadding + pixel - offset - 1), box.Y + 8, 2, 32), color);
        }
    }

    [HarmonyPatch(typeof(ChatBox), "update")]
    public class UpdatePatch
    {
        private static void Postfix(ChatBox __instance, GameTime time)
        {
            if (!__instance.chatBox.Selected) return;
            TextBoxState s = GetState(__instance.chatBox);
            CheckIdleSnapshot(s);

            // Drag selection
            bool mousePressed = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            if (s.IsDragging && mousePressed)
            {
                int mouseX = (int)(Game1.getMousePosition().X / Game1.options.zoomLevel);
                if (mouseX >= __instance.chatBox.X && mouseX <= __instance.chatBox.X + __instance.chatBox.Width - 72)
                {
                    s.CursorIndex = s.SelectionEnd = CalculateCursorFromClick(__instance, mouseX, s);
                }
            }
            if (s.WasMousePressed && !mousePressed) s.IsDragging = false;
            s.WasMousePressed = mousePressed;

            if (!(ModEntry.Instance?.Config.EnableCursorControl ?? false)) return;

            // Repeats
            double now = time.TotalGameTime.TotalSeconds;
            KeyboardState keys = Game1.input.GetKeyboardState();
            float initDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
            float repDelay = ModEntry.Instance.Config.KeyRepeatDelay;
            bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
            bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);

            HandleRepeat(keys.IsKeyDown(Keys.Left), now, initDelay, repDelay, ref s.LastLeftPress, ref s.LastLeftRepeat, () =>
            {
                int nc = ctrl ? GetPrevSegmentStart(s.FullText, s.CursorIndex) : Math.Max(0, s.CursorIndex - 1);
                int snapped = EmojiHelper.SnapToBoundary(s.FullText, nc, -1);
                UpdateSelection(s, snapped, shift);
            });

            HandleRepeat(keys.IsKeyDown(Keys.Right), now, initDelay, repDelay, ref s.LastRightPress, ref s.LastRightRepeat, () =>
            {
                int nc = ctrl ? GetNextSegmentEnd(s.FullText, s.CursorIndex) : Math.Min(s.FullText.Length, s.CursorIndex + 1);
                int snapped = EmojiHelper.SnapToBoundary(s.FullText, nc, 1);
                UpdateSelection(s, snapped, shift);
            });

            HandleRepeat(keys.IsKeyDown(Keys.Home), now, initDelay, repDelay, ref s.LastHomePress, ref s.LastHomeRepeat, () =>
           {
               UpdateSelection(s, 0, shift);
           });

            HandleRepeat(keys.IsKeyDown(Keys.End), now, initDelay, repDelay, ref s.LastEndPress, ref s.LastEndRepeat, () =>
            {
                UpdateSelection(s, s.FullText.Length, shift);
            });

            HandleRepeat(ModEntry.Instance.Config.UndoKeybind.IsDown(), now, initDelay, repDelay,
                ref s.LastUndoPress, ref s.LastUndoRepeat, () =>
                {
                    if (s.UndoStack.TryPop(out HistoryState undoState))
                    {
                        s.RedoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                        RestoreState(__instance.chatBox, s, undoState);
                    }
                });

            HandleRepeat(ModEntry.Instance.Config.RedoKeybind.IsDown(), now, initDelay, repDelay,
                ref s.LastRedoPress, ref s.LastRedoRepeat, () =>
                {
                    if (s.RedoStack.TryPop(out HistoryState redoState))
                    {
                        s.UndoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                        RestoreState(__instance.chatBox, s, redoState);
                    }
                });
        }

        private static void HandleRepeat(bool isDown, double now, float initDelay, float repDelay,
            ref double lastPress, ref double lastRepeat, Action action)
        {
            if (isDown)
            {
                if (lastPress.Equals(0))
                {
                    lastPress = lastRepeat = now;
                    action();
                }
                else if (now - lastPress >= initDelay && now - lastRepeat >= repDelay)
                {
                    lastRepeat = now;
                    action();
                }
            }
            else
            {
                lastPress = 0;
            }
        }
    }

    #endregion
}