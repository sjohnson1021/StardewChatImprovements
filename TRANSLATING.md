# Translating Chat Improvements

All player-facing text lives in `i18n/default.json` (English). Nothing else in the
mod hard-codes a user-visible string.

## Adding a language

Copy `i18n/default.json` to `i18n/<code>.json` and translate the **values only** —
leave the keys and any `{{token}}` placeholders exactly as they are.

Stardew Valley's language codes:

| Code | Language   | Code | Language             |
| ---- | ---------- | ---- | -------------------- |
| `de` | German     | `pt` | Portuguese (Brazil)  |
| `es` | Spanish    | `ru` | Russian              |
| `fr` | French     | `th` | Thai                 |
| `hu` | Hungarian  | `tr` | Turkish              |
| `it` | Italian    | `zh` | Chinese (Simplified) |
| `ja` | Japanese   | `ko` | Korean               |

SMAPI falls back to `default.json` for any key a translation omits, so a partial
file is fine and will not break the mod.

## Notes for translators

- `{{max}}` in `error.message-too-long` is replaced with the configured character
  limit. Keep it, and place it wherever the sentence needs it.
- Config names show in Generic Mod Config Menu next to a control, so short labels
  read best; the longer explanation belongs in the matching `.tooltip` key.
- The chat font is the game's own `SmallFont` for your language, so any character
  the base game can display will render correctly.

## For maintainers

`i18n/default.json` drives a source-generated `I18n` class (via
`Pathoschild.Stardew.ModTranslationClassBuilder`), so a key that is renamed or
removed becomes a **compile error** rather than a silent blank string at runtime.
A readable copy of the generated class is checked in under `Generated/`; it is not
compiled, and is refreshed on each build.
