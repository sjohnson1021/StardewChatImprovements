using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace ChatImprovements;

public sealed class ModConfig
{
    /// <summary>
    ///     The maximum number of characters allowed in a chat message.
    /// </summary>
    public int MaxMessageLength { get; set; } = 500;

    /// <summary>
    ///     Whether to enable horizontal scrolling when typing long messages.
    /// </summary>
    public bool EnableHorizontalScrolling { get; set; } = true;

    /// <summary>
    ///     Whether to enable cursor control (arrow keys for navigation).
    /// </summary>
    public bool EnableCursorControl { get; set; } = true;

    /// <summary>
    ///     Whether to allow clicking URLs in chat messages when the chat box is not open.
    /// </summary>
    public bool AllowUrlClickWhenChatClosed { get; set; } = true;

    /// <summary>
    ///     Maximum number of messages to keep in chat history.
    /// </summary>
    public int MaxChatHistory { get; set; } = 100;

    /// <summary>
    ///     The initial delay in seconds before arrow key repeat starts when holding the key.
    /// </summary>
    public float KeyRepeatInitialDelay { get; set; } = 0.75f;

    /// <summary>
    ///     The delay in seconds between repeated cursor movements when holding arrow keys.
    /// </summary>
    public float KeyRepeatDelay { get; set; } = 0.05f;

    /// <summary>
    ///     Keybind for selecting all text in the chat box.
    /// </summary>
    public KeybindList SelectAllKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.A));

    /// <summary>
    ///     Keybind for copying selected text.
    /// </summary>
    public KeybindList CopyKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.C));

    /// <summary>
    ///     Keybind for cutting selected text.
    /// </summary>
    public KeybindList CutKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.X));

    /// <summary>
    ///     Keybind for pasting text.
    /// </summary>
    public KeybindList PasteKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.V));

    /// <summary>
    ///     Keybind for undoing changes.
    /// </summary>
    public KeybindList UndoKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.Z));

    /// <summary>
    ///     Keybind for redoing changes.
    /// </summary>
    public KeybindList RedoKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.Y));
}