# Changelog

## 1.0.0

Initial public release.

- Raised the chat message limit, configurable from 100 to 1000 characters.
- Chat scrollback: keeps up to 500 messages and scrolls with the mouse wheel.
- Full cursor control: arrow keys, Home and End, with key repeat while held.
- Text selection with Shift and arrow keys, by clicking and dragging, or Ctrl+A.
- Undo and redo while composing a message.
- Copy, cut and paste, all rebindable.
- Ctrl+Left/Right to jump by word, Ctrl+Backspace/Delete to delete by word.
- The chat box scrolls horizontally instead of stopping at the edge.
- A colour button to pick the colour your messages send in, previewed as you type.
- Links in chat are highlighted and open in your browser when clicked.
- Cursor movement and deletion work on grapheme clusters, so combining marks,
  Korean jamo and multi-codepoint emoji are treated as single characters.
- Text is measured with the same font it is drawn with, keeping the caret aligned
  in Japanese, Korean, Chinese and Russian.
- Fully translatable; every player-facing string lives in `i18n/`.
