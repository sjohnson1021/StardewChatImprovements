using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace ChatImprovements;

/// <summary>
/// Configuration options for the Chat Improvements mod.
/// All settings can be modified in-game via Generic Mod Config Menu.
/// </summary>
/// <summary>What happens when the player clicks a link in chat.</summary>
public enum LinkClickAction
{
    /// <summary>Copy the link to the clipboard. The default, because it cannot be abused.</summary>
    Copy,

    /// <summary>Open the link in the player's default browser.</summary>
    Open
}

/// <summary>When a message too long for a vanilla client is broken into several messages.</summary>
public enum VanillaSplitMode
{
    /// <summary>Split only while a connected player does not have this mod. The default.</summary>
    Auto,

    /// <summary>Always split messages that would wrap on a vanilla client.</summary>
    Always,

    /// <summary>Never split. Long messages overlap on the screen of anyone without this mod.</summary>
    Never
}

public sealed class ModConfig
{
    #region Default Value Constants

    // Message Limits - Balance between functionality and performance
    private const int DefaultMaxMessageLength = 500; // Vanilla limit is ~250; doubled for long-form chat
    private const int DefaultMaxChatHistory = 100; // Vanilla keeps ~20; increased for scrollback

    // Keyboard Repeat Timing - Matches OS keyboard repeat behavior
    private const float DefaultInitialDelay = 0.75f; // Standard OS delay before key repeat starts
    private const float DefaultRepeatDelay = 0.05f; // 20 actions/second during hold

    #endregion

    #region General Settings

    /// <summary>
    /// The maximum number of characters allowed in a single chat message.
    /// Default: 500 characters (double vanilla limit).
    /// </summary>
    public int MaxMessageLength { get; set; } = DefaultMaxMessageLength;

    /// <summary>
    /// Maximum number of messages to keep in chat history before old messages are pruned.
    /// Affects memory usage and scroll performance.
    /// Default: 100 messages.
    /// </summary>
    public int MaxChatHistory { get; set; } = DefaultMaxChatHistory;

    #endregion

    #region Feature Toggles

    /// <summary>
    /// Whether to enable horizontal scrolling when typing messages longer than the visible area.
    /// Disabling this reverts to vanilla text wrapping behavior.
    /// Default: true.
    /// </summary>
    public bool EnableHorizontalScrolling { get; set; } = true;

    /// <summary>
    /// Whether to enable enhanced cursor control (arrow keys, Home/End, Ctrl+Arrow for word jumping).
    /// Disabling this reverts to vanilla text input behavior.
    /// Default: true.
    /// </summary>
    public bool EnableCursorControl { get; set; } = true;

    /// <summary>
    /// Whether to render the Message Color ColorPickerMenu
    /// Disabling this does not unset the defaultChatColor.
    /// Default: true.
    /// </summary>
    public bool EnableMessageColorButton { get; set; } = true;

    /// <summary>
    /// Whether to allow clicking URLs in chat messages when the chat box is minimized.
    /// If false, URLs can only be clicked when the chat box is open.
    /// Default: true.
    /// </summary>
    /// <remarks>
    ///     Off by default: the mod does not consume the click, so clicking a link with the chat
    ///     box closed also performs the normal game action underneath it.
    /// </remarks>
    public bool AllowUrlClickWhenChatClosed { get; set; }

    /// <summary>
    /// Whether to break a message too long for a vanilla client into several shorter ones
    /// before sending it.
    /// </summary>
    /// <remarks>
    ///     A client without this mod measures a message's height from one text wrap and draws it
    ///     with another. They agree for anything vanilla-length, because it never wraps, and
    ///     disagree once it does -- the drawn text is taller than the space reserved for it and
    ///     runs over the messages around it. That client cannot be patched, so the only place to
    ///     fix it is here, before the message is sent.
    ///
    ///     Defaults to <see cref="VanillaSplitMode.Auto" />: nobody pays for the split until
    ///     someone is connected who needs it.
    /// </remarks>
    public VanillaSplitMode SplitLongMessages { get; set; } = VanillaSplitMode.Auto;

    /// <summary>What clicking a link does.</summary>
    /// <remarks>
    ///     Copying is the default. Links in chat come from other players, and opening one
    ///     straight into a browser on a single click is not something the player opted into.
    ///     Copying lets them look before they go.
    /// </remarks>
    public LinkClickAction LinkClickBehavior { get; set; } = LinkClickAction.Copy;

    #endregion

    #region Input Settings

    /// <summary>
    /// The initial delay (in seconds) before arrow key repeat starts when holding a key.
    /// Increase this if you find the cursor moves too quickly when holding arrow keys.
    /// Default: 0.75 seconds.
    /// </summary>
    public float KeyRepeatInitialDelay { get; set; } = DefaultInitialDelay;

    /// <summary>
    /// The delay (in seconds) between repeated cursor movements when holding arrow keys.
    /// Lower values = faster repeat. Decrease this for faster navigation.
    /// Default: 0.05 seconds (20 movements per second).
    /// </summary>
    public float KeyRepeatDelay { get; set; } = DefaultRepeatDelay;

    #endregion

    #region Keybinds

    /// <summary>
    /// Keybind for selecting all text in the chat box.
    /// Default: Ctrl+A (Windows/Linux standard).
    /// </summary>
    public KeybindList SelectAllKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.A));

    /// <summary>
    /// Keybind for copying selected text to the system clipboard.
    /// Default: Ctrl+C (universal standard).
    /// </summary>
    public KeybindList CopyKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.C));

    /// <summary>
    /// Keybind for cutting selected text (copy + delete).
    /// Default: Ctrl+X (universal standard).
    /// </summary>
    public KeybindList CutKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.X));

    /// <summary>
    /// Keybind for pasting text from the system clipboard.
    /// Default: Ctrl+V (universal standard).
    /// </summary>
    public KeybindList PasteKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.V));

    /// <summary>
    /// Keybind for undoing the last text change.
    /// Default: Ctrl+Z (universal standard).
    /// </summary>
    public KeybindList UndoKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.Z));

    /// <summary>
    /// Keybind for redoing the last undone change.
    /// Default: Ctrl+Y (Windows standard).
    /// </summary>
    public KeybindList RedoKeybind { get; set; } = new(new Keybind(SButton.LeftControl, SButton.Y));

    #endregion
}