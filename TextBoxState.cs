using System.Collections.Generic;

namespace ChatImprovements;

/// <summary>
/// Categorizes user operations for smart undo/redo snapshotting.
/// Different operation types trigger snapshots at different times.
/// </summary>
internal enum OperationType
{
    None, // No recent operation
    Typing, // Continuous character input
    Backspace, // Continuous deletion backwards
    Delete, // Continuous deletion forwards
    Paste, // Discrete clipboard operation
    CursorMove, // Cursor repositioning
    Emoji // Emoji insertion
}

/// <summary>
/// Immutable snapshot of text box state for undo/redo.
/// </summary>
internal readonly struct HistoryState
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

/// <summary>
/// Tracks the complete state of a chat text box including cursor position,
/// selection, undo/redo history, and scrolling offset.
/// </summary>
internal class TextBoxState
{
    // Text Content
    public string FullText = "";

    // Cursor & Selection
    public int CursorIndex;
    public int SelectionStart;
    public int SelectionEnd;

    // Scrolling
    public float ScrollOffset;

    // Mouse Interaction
    public bool IsDragging;
    public bool WasMousePressed;

    // Keyboard Repeat Timing
    public double LastLeftPress, LastRightPress, LastHomePress, LastEndPress;
    public double LastLeftRepeat, LastRightRepeat, LastHomeRepeat, LastEndRepeat;
    public double LastUndoPress, LastRedoPress;
    public double LastUndoRepeat, LastRedoRepeat;

    // Undo/Redo System
    public readonly Stack<HistoryState> UndoStack = new();
    public readonly Stack<HistoryState> RedoStack = new();

    // Smart Snapshot Tracking
    public double LastSnapshotTime;
    public double LastTypingTime;
    public int CharsSinceSnapshot;
    public int LastSnapshotCursor = -1;
    public OperationType LastOperation = OperationType.None;

    public void Reset()
    {
        FullText = "";
        CursorIndex = SelectionStart = SelectionEnd = 0;
        ScrollOffset = 0;
        UndoStack.Clear();
        RedoStack.Clear();
        CharsSinceSnapshot = 0;
        LastOperation = OperationType.None;
        IsDragging = false;
        WasMousePressed = false;
    }
}
