# Changelog

## 1.1.0

Compatibility fixes for Chat Time and Item Chat Link, and a fix for long messages
overlapping on the screens of players who don't have the mod.

- Added a Simplified Chinese translation, contributed by
  [@BlackRosePetals](https://github.com/BlackRosePetals) (CNSCZJ on Nexus).
- Fixed long messages overlapping the messages around them for a player without
  the mod. Such a client reserves a message's height by advancing the font's line
  spacing per line but draws it stepping down by slightly more, so each line
  drifts further past the space reserved for it until the message covers the one
  below. Messages are now split before sending, once they would wrap past three
  lines on such a client. Controlled by the new `SplitLongMessages` setting, which
  splits only while such a player is connected. A split message arrives as several
  messages for everyone, since the game cannot send one player a different message
  than another without it becoming a whisper.
  ([#1](https://github.com/sjohnson1021/StardewChatImprovements/issues/1))
- Fixed Chat Time's timestamp appearing in the chat input and pushing the caret
  and selection out of place. The text box now parses its own text instead of
  routing it through the game's message parser, where other mods' patches sit.
- Fixed the sender name being underlined in the wrong place, and split across two
  lines, when another mod adds text ahead of it. The name is now located in the
  message rather than assumed to be the first thing in it.
- Fixed item links from Item Chat Link not being inspectable. Both mods took over
  the same draw method and only one could win, so whether its tooltips worked came
  down to patch order. Messages carrying an item link are now left for it to draw.
- Fixed messages holding an Item Chat Link link running past the right edge of the
  chat box. That mod wraps at a hardcoded 888px measured from the box's left edge,
  and only after drawing the segment that crossed it. Those messages are now
  pre-broken to a width that fits, measured by what actually gets painted -- the
  hidden item marker does not count toward the line, and a run of links written
  back to back is measured in full rather than by its first link alone.
- Fixed an item link being torn in half when the item's name contains a space,
  which left "[Small Plant]" rendering as two pieces of plain text.
- Fixed inserting an item link re-adding every link already in the box, and
  dragging the caret to the end. Vanilla implements `setText` as a reset followed
  by a text-input call, which this mod treated as an insertion; it is now handled
  as the replacement it is, and a link lands at the caret rather than at the end.
- Fixed messages reserving the wrong height and overlapping each other. Height and
  drawing were separate passes over different data at different widths, and now
  share one layout.
- Fixed long messages running off the right edge of the chat box instead of
  wrapping. A message body was laid out as a single unbreakable run; it is now
  measured a word at a time.
- Fixed messages being wrapped twice, which broke them into stray one-word lines.
  Wrapping now happens once, where the message is drawn, so it accounts for
  anything another mod has added to the message.
- Fixed a line break in a sent message stranding the sender's name alone on the
  first line for everyone else. Line breaks are flattened as the message goes out,
  and pasting multi-line text collapses them to spaces.
- The drawing patches now run after other mods', so a mod that only watches the
  chat box or a message being drawn is no longer skipped.

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
- Links in chat are highlighted; clicking one copies it to the clipboard by
  default, or opens it in the browser if you switch `LinkClickBehavior`.
- Cursor movement and deletion work on grapheme clusters, so combining marks,
  Korean jamo and multi-codepoint emoji are treated as single characters.
- Text is measured with the same font it is drawn with, keeping the caret aligned
  in Japanese, Korean, Chinese and Russian.
- Fully translatable; every player-facing string lives in `i18n/`.
