## Summary of Accomplishments

We've successfully enhanced the Stardew Valley chat system with the following features:

- **Horizontal Scrolling**: Long messages now scroll horizontally within the chat input box, keeping the cursor visible and preventing text from being cut off prematurely.
- **Cursor Control**: Implemented full text editing capabilities:
  - Typing inserts text at the current cursor position.
  - Backspace deletes the character before the cursor.
  - Left/Right arrow keys move the cursor through the text (with configurable key repeat for holding).
  - Home/End keys jump to beginning/end of message.
  - Mouse clicks position the cursor at the clicked location.
- **Text Selection**: 
  - Shift+Arrow keys for character-by-character selection.
  - Ctrl+Shift+Arrow keys for word/symbol selection (words as contiguous alphanumeric groups, symbols as individual characters).
  - Ctrl+A to select all text.
  - Selection highlighting with a semi-transparent blue rectangle.
- **Clipboard Operations**: 
  - Ctrl+C to copy selected text.
  - Ctrl+X to cut selected text.
  - Ctrl+V to paste clipboard contents, replacing any selection.
  - Cross-platform clipboard support using SDL2, with Wayland fallback on Linux for setting clipboard text.
- **Text Editing**:
  - Delete key removes characters or selections.
  - Selection replacement on typing, pasting, or inserting text.
- **Text Clipping**: Uses `GraphicsDevice.ScissorRectangle` to contain text within the input box boundaries, preventing overflow onto other UI elements.
- **UI Adjustments**: Fine-tuned padding to maximize usable text area while avoiding overlap with the emoji icon.
- **Character Limits**: Enforced configurable message length limits with user feedback.
- **Message Clearing**: Input box clears after successful message send, but persists on failed sends for editing.
- **Framework Integration**: Used Harmony for runtime patching of game methods, SMAPI for mod loading and configuration.

The mod builds successfully and allows typing, editing, selecting, copying, cutting, pasting, and sending long messages without crashing the game or network.

### Text Clipping Implementation Notes
- **Strategy**: Uses `GraphicsDevice.ScissorRectangle` with proper `RasterizerState` setup.
- **Key Insight**: Must end the current `SpriteBatch`, set scissor/rasterizer, begin new batch with `ScissorTestEnable = true`, draw, then restore state.
- **Advantages**: Simpler than substring calculations; handles clipping at the GPU level.
- **Previous Approach**: Manual substring extraction and position adjustments - more complex and error-prone.
- **Future Reference**: Always save/restore `ScissorRectangle` and `RasterizerState`; restart `SpriteBatch` with correct parameters for clipping to work.

### Clipboard Implementation Notes
- **Cross-Platform Strategy**: Uses SDL2 for clipboard access (available via game's SDL2 library).
- **Linux Wayland Fallback**: On Linux, attempts `wl-copy`/`wl-paste` first for setting/getting clipboard (resolves Wayland compatibility issues), falls back to SDL2 if unavailable.
- **UTF-8 Handling**: SDL2 requires manual UTF-8 encoding for setting; getting returns UTF-8 strings.
- **Selection Integration**: Copy/cut operations use selected text; paste replaces any active selection.
- **Key Bindings**: Configurable via GMCM for customization.

## Onboarding Message for Next Agent

Welcome to the Chat Improvements mod project! This mod aims to enhance Stardew Valley's chat system by enabling longer messages with horizontal scrolling and advanced cursor control, while maintaining compatibility with multiplayer and UI consistency.

### Project Structure
- **Root**: `/home/seanj/GOG Games/Stardew Valley/ChatImprovements/`
- **Key Files**:
  - ChatTextBoxPatches.cs: Contains Harmony patches for `ChatTextBox` and `ChatBox` classes.
  - `ModEntry.cs`: SMAPI mod entry point, handles configuration and patching.
  - `ModConfig.cs`: User-configurable settings (e.g., max message length, scrolling toggle).
- **Dependencies**: SMAPI 4.0+, Harmony 2.2.2, Pathoschild.Stardew.ModdingAPI 4.0.0.
- **Build**: Use `dotnet build` in the project directory. Output goes to `bin/Debug/net6.0/`.

### Frameworks and Techniques
- **SMAPI**: Use for mod initialization, configuration menus (GMCM), and logging. Access via `this.Helper` in `ModEntry`.
- **Harmony**: Apply patches with `[HarmonyPatch]` attributes. Use `Prefix` to modify method behavior, `Postfix` for after execution. Access private fields with `AccessTools.FieldRefAccess`.
- **XNA/MonoGame**: For drawing, use `SpriteBatch.DrawString` with fonts from `ChatBox.messageFont()`. Handle input via `Game1.input`.
- **Decompiled Source**: Reference StardewValleyDecompiled for method signatures and logic. Key classes: `ChatTextBox`, `ChatBox`, `ChatMessage`.

### Current State
- Horizontal scrolling with text clipping implemented and working correctly.
- Full cursor control with arrow keys (including key repeat), Home/End, and mouse positioning.
- Text selection with Shift+Arrow (character) and Ctrl+Shift+Arrow (word/symbol) keys.
- Clipboard operations: Copy (Ctrl+C), Cut (Ctrl+X), Paste (Ctrl+V) with cross-platform support.
- Selection highlighting and replacement on edit operations.
- Configurable key repeat delays via GMCM.
- Message clearing after send; persistence on failed sends.
- Test in-game: Type long messages, use all navigation and selection methods, copy/cut/paste, send without issues.

### Known Issues
- Emoji support incomplete; handle as `[emoji]` strings in `fullText`.

### Next Steps
- Re-enable emojis by parsing `[number]` in `fullText` and integrating with the game's emoji system.
- Test multiplayer compatibility thoroughly (ensure selections and clipboard don't interfere with network messages).
- Add any remaining keybinds or customization options via GMCM.
- Performance testing with very long messages and rapid key presses.
- Consider undo/redo functionality if feasible.

Good luck— the foundation is solid, with full text editing, selection, and clipboard support implemented! If you need the roadmap, check BetterChat-Roadmap.md.