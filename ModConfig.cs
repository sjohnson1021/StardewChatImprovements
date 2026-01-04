using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace ChatImprovements
{
    public sealed class ModConfig
    {
        /// <summary>
        /// The maximum number of characters allowed in a chat message.
        /// </summary>
        public int MaxMessageLength { get; set; } = 1000;

        /// <summary>
        /// Whether to enable horizontal scrolling when typing long messages.
        /// </summary>
        public bool EnableHorizontalScrolling { get; set; } = true;

        /// <summary>
        /// Whether to enable cursor control (arrow keys for navigation).
        /// </summary>
        public bool EnableCursorControl { get; set; } = true;

        /// <summary>
        /// The initial delay in seconds before arrow key repeat starts when holding the key.
        /// </summary>
        public float KeyRepeatInitialDelay { get; set; } = 0.75f;

        /// <summary>
        /// The delay in seconds between repeated cursor movements when holding arrow keys.
        /// </summary>
        public float KeyRepeatDelay { get; set; } = 0.05f;

        /// <summary>
        /// Keybind for copying selected text.
        /// </summary>
        public KeybindList CopyKeybind { get; set; } = new KeybindList(new Keybind(SButton.LeftControl, SButton.C));

        /// <summary>
        /// Keybind for cutting selected text.
        /// </summary>
        public KeybindList CutKeybind { get; set; } = new KeybindList(new Keybind(SButton.LeftControl, SButton.X));

        /// <summary>
        /// Keybind for pasting text.
        /// </summary>
        public KeybindList PasteKeybind { get; set; } = new KeybindList(new Keybind(SButton.LeftControl, SButton.V));
    }
}
