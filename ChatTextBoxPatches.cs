using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
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
        public double LastUndoPress, LastRedoPress;
        public double LastUndoRepeat, LastRedoRepeat;
        public float ScrollOffset;
        public int SelectionEnd;
        public int SelectionStart;

        // Undo/Redo Stacks
        public readonly Stack<HistoryState> UndoStack = new();
        public readonly Stack<HistoryState> RedoStack = new();

        // Smart snapshot tracking
        public double LastSnapshotTime;
        public double LastTypingTime;
        public int CharsSinceSnapshot;
        public int LastSnapshotCursor = -1;
        public OperationType LastOperation = OperationType.None;
    }

    private enum OperationType
    {
        None,
        Typing,
        Backspace,
        Delete,
        Paste,
        CursorMove,
        Emoji
    }

    private readonly struct HistoryState
    {
        public readonly string Text;
        public readonly int Cursor;
        public readonly int SelectionStart;
        public readonly int SelectionEnd;

        public HistoryState(string text, int cursor, int selectionStart, int selectionEnd)
        {
            Text = text;
            Cursor = cursor;
            SelectionStart = selectionStart;
            SelectionEnd = selectionEnd;
        }
    }

    private static TextBoxState GetState(ChatTextBox box)
    {
        if (States.TryGetValue(box, out TextBoxState? state)) return state;
        state = new TextBoxState();
        States[box] = state;
        return state;
    }

    #endregion

    #region Smart Snapshot Logic

    /// <summary>
    /// Intelligently decide whether to take a snapshot based on context.
    /// </summary>
    private static void MaybeSnapshot(TextBoxState s, OperationType currentOp, bool force = false)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        
        // Always snapshot if forced (discrete operations like paste, emoji)
        if (force)
        {
            TakeSnapshot(s);
            s.LastOperation = currentOp;
            s.CharsSinceSnapshot = 0;
            s.LastSnapshotCursor = s.CursorIndex;
            s.LastSnapshotTime = now;
            return;
        }

        // Snapshot on operation type change (typing → backspace, etc.)
        if (s.LastOperation != OperationType.None && s.LastOperation != currentOp)
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
        }

        // Snapshot on cursor position jump (user moved cursor)
        if (currentOp == OperationType.Typing && s.LastSnapshotCursor <= -1)
        {
            // If cursor jumped (not sequential typing), snapshot before typing at new position
            if (s.CursorIndex != s.LastSnapshotCursor && 
                s.CursorIndex != s.LastSnapshotCursor + s.CharsSinceSnapshot)
            {
                TakeSnapshot(s);
                s.CharsSinceSnapshot = 0;
            }
        }

        // Snapshot every 10 characters during continuous typing
        if (currentOp == OperationType.Typing && s.CharsSinceSnapshot >= 10)//SnapshotCharacterInterval
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
        }

        s.LastOperation = currentOp;
        s.LastSnapshotTime = now;
        s.LastSnapshotCursor = s.CursorIndex;
    }

    /// <summary>
    /// Check if enough time has passed for an idle snapshot (called from Update).
    /// </summary>
    private static void CheckIdleSnapshot(TextBoxState s)
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        
        // If typing stopped for 1 second and we have unsaved changes, snapshot
        if (s.LastOperation != OperationType.None && 
            now - s.LastSnapshotTime >= 1.5 && //Snapshot Time Interval - Time between snapshots
            s.CharsSinceSnapshot > 0)
        {
            TakeSnapshot(s);
            s.CharsSinceSnapshot = 0;
            s.LastOperation = OperationType.None;
        }
    }

    private static void TakeSnapshot(TextBoxState s)
    {
        // Don't snapshot if nothing changed
        if (s.UndoStack.Count > 0)
        {
            var last = s.UndoStack.Peek();
            if (last.Text == s.FullText && 
                last.Cursor == s.CursorIndex && 
                last.SelectionStart == s.SelectionStart && 
                last.SelectionEnd == s.SelectionEnd)
            {
                return;
            }
        }

        s.UndoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
        
        // Limit history to 100 states
        if (s.UndoStack.Count >= 500)
        {
            var items = new List<HistoryState>(s.UndoStack);
            items.RemoveAt(items.Count - 1); // Remove oldest
            s.UndoStack.Clear();
            items.Reverse(); // Stack stores newest first, so reverse to rebuild
            foreach (var item in items)
                s.UndoStack.Push(item);
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

        Regex emojiRegex = new(@"\[\d{2,3}\]");

        foreach (Match match in emojiRegex.Matches(text))
        {
            int start = match.Index;
            int end = match.Index + match.Length;

            if (cursor > start && cursor < end)
                return direction < 0 ? start :
                    direction > 0 ? end :
                    cursor - start < end - cursor ? start : end;
        }

        return cursor;
    }

    private static (int start, int end) GetEmojiToDelete(string text, int pos, bool isBackspace)
    {
        if (string.IsNullOrEmpty(text) || pos < 0 || pos > text.Length) return (-1, -1);

        Regex emojiRegex = new(@"\[\d{2,3}\]");

        foreach (Match match in emojiRegex.Matches(text))
        {
            int start = match.Index;
            int end = match.Index + match.Length;

            if ((isBackspace && pos > start && pos <= end) ||
                (!isBackspace && pos >= start && pos < end))
                return (start, end);
        }

        return (-1, -1);
    }

    #endregion

    #region Text Operations

    private static void InsertText(ChatTextBox box, string text)
    {
        TextBoxState s = GetState(box);
        
        // Smart snapshot before inserting
        MaybeSnapshot(s, OperationType.Typing);

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
        s.CharsSinceSnapshot += text.Length;
        s.LastTypingTime = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        
        RebuildText(box, s);
    }

    private static void DeleteSelection(ChatTextBox box)
    {
        TextBoxState s = GetState(box);
        if (s.SelectionStart == s.SelectionEnd) return;
        
        // Always snapshot before deleting selection (discrete operation)
        MaybeSnapshot(s, OperationType.Delete, force: true);

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
            if (!__instance.Selected) 
            return true;
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

            // Emoji is a discrete operation - force snapshot
            MaybeSnapshot(s, OperationType.Emoji, force: true);
            
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
                // Smart snapshot for backspace
                MaybeSnapshot(s, OperationType.Backspace);
                
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
                (__instance.choosingEmoji && __instance.emojiMenu.isWithinBounds(x, y))) return false;
            {
                TextBoxState s = GetState(__instance.chatBox);

                if (s.IsDragging) return false;
                int newCursor = CalculateCursorFromClick(__instance, x, s);

                bool shift = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                             Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);

                if (!shift)
                {
                    s.CursorIndex = newCursor;
                    s.SelectionStart = newCursor;
                    s.IsDragging = true;
                }

                s.SelectionEnd = newCursor;
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
            bool shift = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift) ||
                         Game1.input.GetKeyboardState().IsKeyDown(Keys.RightShift);
            bool ctrl = Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftControl) ||
                        Game1.input.GetKeyboardState().IsKeyDown(Keys.RightControl);
            double time = Game1.currentGameTime.TotalGameTime.TotalSeconds;

            // Handle Keybinds
            if (ModEntry.Instance.Config.UndoKeybind.JustPressed())
            {
                if (s.UndoStack.TryPop(out HistoryState undoState))
                {
                    s.RedoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                    RestoreState(__instance.chatBox, s, undoState);
                }

                return false;
            }

            if (ModEntry.Instance.Config.RedoKeybind.JustPressed())
            {
                if (s.RedoStack.TryPop(out HistoryState redoState))
                {
                    s.UndoStack.Push(new HistoryState(s.FullText, s.CursorIndex, s.SelectionStart, s.SelectionEnd));
                    RestoreState(__instance.chatBox, s, redoState);
                }

                return false;
            }

            if (ModEntry.Instance.Config.CopyKeybind.JustPressed())
            {
                if (s.SelectionStart != s.SelectionEnd)
                {
                    SetClipboard(s.FullText.Substring(
                        Math.Min(s.SelectionStart, s.SelectionEnd),
                        Math.Abs(s.SelectionEnd - s.SelectionStart)));
                }

                return false;
            }

            if (ModEntry.Instance.Config.CutKeybind.JustPressed())
            {
                if (s.SelectionStart != s.SelectionEnd)
                {
                    SetClipboard(s.FullText.Substring(
                        Math.Min(s.SelectionStart, s.SelectionEnd),
                        Math.Abs(s.SelectionEnd - s.SelectionStart)));
                    DeleteSelection(__instance.chatBox);
                }

                return false;
            }

            if (ModEntry.Instance.Config.PasteKeybind.JustPressed())
            {
                // Paste is a discrete operation
                MaybeSnapshot(s, OperationType.Paste, force: true);
                InsertText(__instance.chatBox, GetClipboard());
                return false;
            }

            if (ModEntry.Instance.Config.SelectAllKeybind.JustPressed())
            {
                s.SelectionStart = 0;
                s.SelectionEnd = s.CursorIndex = s.FullText.Length;
                return false;
            }

            // Handle cursor movement
            int newCursor = s.CursorIndex;
            bool handled = false;

            switch (key)
            {
                case Keys.Left:
                    // Mark cursor movement
                    if (s.LastOperation == OperationType.Typing)
                        s.LastOperation = OperationType.CursorMove;
                    
                    newCursor = ctrl ? GetPrevSegmentStart(s.FullText, s.CursorIndex) :
                        s.CursorIndex > 0 ? s.CursorIndex - 1 : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, -1);
                    s.LastLeftPress = time;
                    handled = true;
                    break;
                case Keys.Right:
                    if (s.LastOperation == OperationType.Typing)
                        s.LastOperation = OperationType.CursorMove;
                    
                    newCursor = ctrl ? GetNextSegmentEnd(s.FullText, s.CursorIndex) :
                        s.CursorIndex < s.FullText.Length ? s.CursorIndex + 1 : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, 1);
                    s.LastRightPress = time;
                    handled = true;
                    break;
                case Keys.Home:
                    if (s.LastOperation == OperationType.Typing)
                        s.LastOperation = OperationType.CursorMove;
                    
                    newCursor = 0;
                    s.LastHomePress = time;
                    handled = true;
                    break;
                case Keys.End:
                    if (s.LastOperation == OperationType.Typing)
                        s.LastOperation = OperationType.CursorMove;
                    
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
                        MaybeSnapshot(s, OperationType.Delete);
                        
                        if (ctrl)
                        {
                            (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, false);
                            int segEnd = GetNextSegmentEnd(s.FullText, s.CursorIndex);
                            s.FullText = emoji.start != -1
                                ? s.FullText.Remove(emoji.start, emoji.end - emoji.start)
                                : s.FullText.Remove(s.CursorIndex, segEnd - s.CursorIndex);
                        }
                        else
                        {
                            (int start, int end) emoji = GetEmojiToDelete(s.FullText, s.CursorIndex, false);
                            s.FullText = emoji.start != -1
                                ? s.FullText.Remove(emoji.start, emoji.end - emoji.start)
                                : s.FullText.Remove(s.CursorIndex, 1);
                        }

                        s.CharsSinceSnapshot++;
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
            var t = ModEntry.Instance?.Helper.Translation;
            __instance.addErrorMessage(
                string.Format(t?.Get("hud.messageTooLong") ?? "Message too long! Maximum {0} characters.",
                    ModEntry.Instance?.Config.MaxMessageLength ?? 0));
            return false;
        }

        private static void Postfix(TextBox sender)
        {
            if (sender is not ChatTextBox box) return;
            TextBoxState s = GetState(box);

            s.FullText = "";
            s.CursorIndex = s.SelectionStart = s.SelectionEnd = 0;
            s.ScrollOffset = 0;
            s.UndoStack.Clear();
            s.RedoStack.Clear();
            s.CharsSinceSnapshot = 0;
            s.LastOperation = OperationType.None;

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

            ChatMessage msg = new();
            msg.parseMessageForEmoji(s.FullText);
            msg.color = ChatMessage.getColorFromName(Game1.player.defaultChatColor);
            msg.language = LocalizedContentManager.CurrentLanguageCode;

            float totalWidth = msg.message.Sum(snippet => snippet.myLength);

            if (font is null)
                return true;

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
            
            // Check for idle snapshot (1 second of no typing)
            CheckIdleSnapshot(s);
            
            MouseState mouseState = Game1.input.GetMouseState();
            bool isMousePressed = mouseState.LeftButton == ButtonState.Pressed;

            // Handle drag selection
            if (s.IsDragging && isMousePressed)
            {
                Point mouse = Game1.getMousePosition();
                int mouseX = (int)(mouse.X / Game1.options.zoomLevel);

                if (mouseX >= __instance.chatBox.X && mouseX <= __instance.chatBox.X + __instance.chatBox.Width - 72)
                {
                    int newCursor = CalculateCursorFromClick(__instance, mouseX, s);
                    s.SelectionEnd = newCursor;
                    s.CursorIndex = newCursor;
                }
            }

            if (s.WasMousePressed && !isMousePressed) s.IsDragging = false;

            s.WasMousePressed = isMousePressed;

            if (!ModEntry.Instance.Config.EnableCursorControl) return;

            double now = time.TotalGameTime.TotalSeconds;
            KeyboardState keys = Game1.input.GetKeyboardState();
            bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
            bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);

            float initDelay = ModEntry.Instance.Config.KeyRepeatInitialDelay;
            float repDelay = ModEntry.Instance.Config.KeyRepeatDelay;

            HandleRepeat(keys.IsKeyDown(Keys.Left), now, initDelay, repDelay,
                ref s.LastLeftPress, ref s.LastLeftRepeat,
                () =>
                {
                    int newCursor = ctrl
                        ? GetPrevSegmentStart(s.FullText, s.CursorIndex)
                        : s.CursorIndex > 0
                            ? s.CursorIndex - 1
                            : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, -1);
                    if (newCursor != s.CursorIndex)
                        UpdateSelection(s, newCursor, shift);
                });

            HandleRepeat(keys.IsKeyDown(Keys.Right), now, initDelay, repDelay,
                ref s.LastRightPress, ref s.LastRightRepeat,
                () =>
                {
                    int newCursor = ctrl
                        ? GetNextSegmentEnd(s.FullText, s.CursorIndex)
                        : s.CursorIndex < s.FullText.Length
                            ? s.CursorIndex + 1
                            : s.CursorIndex;
                    newCursor = SnapCursorToEmojiBoundary(s.FullText, newCursor, 1);
                    if (newCursor != s.CursorIndex)
                        UpdateSelection(s, newCursor, shift);
                });

            HandleRepeat(keys.IsKeyDown(Keys.Home), now, initDelay, repDelay,
                ref s.LastHomePress, ref s.LastHomeRepeat,
                () =>
                {
                    if (s.CursorIndex != 0)
                        UpdateSelection(s, 0, shift);
                });

            HandleRepeat(keys.IsKeyDown(Keys.End), now, initDelay, repDelay,
                ref s.LastEndPress, ref s.LastEndRepeat,
                () =>
                {
                    if (s.CursorIndex != s.FullText.Length)
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

        private static void HandleRepeat(bool isDown, double now, float initDelay, float repDelay,
            ref double lastPress, ref double lastRepeat, Action action)
        {
            if (isDown)
            {
                if (lastPress.Equals(0))
                {
                    lastPress = now;
                    lastRepeat = now;
                }

                if (now - lastPress >= initDelay && now - lastRepeat >= repDelay)
                {
                    action();
                    lastRepeat = now;
                }
            }
            else
            {
                lastPress = lastRepeat = 0;
            }
        }

        #endregion
    }
}
#pragma warning restore CS8602