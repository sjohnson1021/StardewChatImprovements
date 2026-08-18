# Contributing to Chat Improvements

Welcome to the **Chat Improvements** project! This document outlines the standards, guidelines, and philosophy for contributing to this Stardew Valley mod.

## 1. Project Philosophy

**Goal**: Create a seamless, "Vanilla+" chat experience for Stardew Valley 1.6+ (SMAPI 4+).
**Core Tenet**: Functionality > aesthetics, but Readability > brevity.
**Risk Tolerance**: Low. This mod hooks into critical text input methods. If we break, the player cannot type. Harmony patches must be defensive and fail gracefully.

## 2. Project Structure

- **ChatImprovements/**
  - `ChatImprovements.csproj`: .NET project file.
  - `manifest.json`: SMAPI manifest.
  - `ModEntry.cs`: Entry point, config loading, Harmony initialization.
  - `ModConfig.cs`: Configuration model.
  - `ChatMessagePatches.cs`: Render clickable URLs, pointer cursor interactions.
  - `ChatBoxPatches.cs`: Chat history scrolling, rendering, message wrapping.
  - `ChatTextBoxPatches.cs`: Input handling, horizontal scrolling, cursor, selection, clipboard.
  - `ColorPickerMenu.cs`: Custom UI for color selection.
  - `i18n/`: Translation files (default.json).
  - `assets/`: Textures and other binary assets.

## 3. Code Actions & Organization

### 3.1 Standard File Layout
To ensure logical flow, files should adhere to this member ordering:

1. **Dependencies**: `using` statements (cleaned and sorted).
2. **Namespace**: `ChatImprovements`.
3. **Class Definition**:
    - **Constants**: `const` and `static readonly`. No magic numbers in code.
    - **State**: Private fields `_camelCase`.
    - **Configuration**: Public properties or references to `ModConfig`.
    - **Lifecycle**: Constructors, `Entry`, or `Initialize`.
    - **Core Logic**: Primary public methods.
    - **Event Handlers**: SMAPI event subscribers (e.g., `OnButtonPressed`).
    - **Helpers**: Private utility methods.

### 3.2 Regions
Use `#region` to group semantic sections (e.g., `Harmony Patches`, `Input Handling`), but do not use them to hide messy code.

## 4. C# Styling & Rules

- **Framework**: C# 10 / .NET 6.
- **Null Safety**: Enabled. Use nullable reference types (`?`) and operators (`??`) freely.
- **Implicit Usings**: Enabled.
- **Syntax Preferences**:
  - Use `var` when type is obvious; explicit types when ambiguous.
  - Use string interpolation `$"..."`.
  - Prefer switch expressions `x switch { ... }`.
  - **Refactoring**: Use Guard Clauses to avoid deep nesting.
  - **Naming**: PascalCase for public members/methods; camelCase for private fields (`_field`).

## 5. Harmony Patching Guidelines

- **Safety First**: Wrap patch logic in `try/catch`. Log errors once, then degrade gracefully. Do not crash the game.
- **Prefix vs. Postfix**: 
  - **Prefix**: To block execution (`return false`), modify arguments, or run pre-logic.
  - **Postfix**: To modify results or render overlays (e.g., shadows).
- **Performance**: 
  - Do NOT instantiate `new` objects (Textures, Fonts) in `Draw` patches. Cache them.
  - Avoid heavy logging in hot paths (Update/Draw).

## 6. Domain-Specific Logic

### Text & Input
- **Cursor**: Manually managed. Ensure `caretIndex` is always within `0` to `text.Length`.
- **Clipboard**: 
  - Use platform-specific handling where possible (SDL2, `wl-copy` for Wayland).
  - Watch out for "Double Paste" bugs (conflicts between OS and Game inputs).
- **Scrolling**: Horizontal scrolling relies on string width measurements. Cache these when possible.

### UI & Rendering
- **Faux Bold**: simulate bolding by drawing text with a shadow offset.
- **Chat History**: Implement a soft cap (e.g., 500 messages) to prevent memory creep.
- **Localization**: All user-facing strings must use `I18n` (SMAPI `Helper.Translation`).

## 7. Development & Tools

- **Dependencies**:
  - SMAPI 4.0+
  - Harmony 2.2.2+
  - *Optional*: Generic Mod Config Menu (GMCM)
- **Build**: Run `dotnet build`. Release artifacts include the DLL, `manifest.json`, `i18n`, and `assets`.

## 8. Testing Strategy

Since automated UI tests are difficult, use this manual checklist for every PR:

1. **The "Typing" Test**: Type a long sentence that wraps. Edit in the middle (insert/delete).
2. **The "Copy/Paste" Test**: Copy/Paste text in/out of the game and between OS/Game.
3. **The "History" Test**: Fill the chat until it scrolls. Verify old messages are accessible usually via scroll.
4. **The "Feature" Test**: Toggle features in GMCM (e.g., timestamps, background color) and verify immediate updates.
