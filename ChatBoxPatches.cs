using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

internal class ChatBoxScrollPatches
{
    private static readonly Dictionary<ChatBox, ScrollState> ScrollStates = new();
    private static readonly FieldInfo s_MessagesField = AccessTools.Field(typeof(ChatBox), "messages");

    // Color Picker State
    private static ClickableTextureComponent? _colorPickerButton;
    private static ColorPickerMenu? _activeColorMenu;

    private sealed class ScrollState
    {
        public float ScrollOffset { get; set; }
        public float TargetScrollOffset { get; set; }
        public bool WasSelected { get; set; }

        public bool WasColorButtonEnabled { get; set; }
        public int LastChatBoxWidth { get; set; }
        public int LastEmojiIconY { get; set; }

        // Analyzer suggests making this static; it stays an instance member so ScrollState
        // reads as a single unit of per-chat-box state.
#pragma warning disable CA1822
        public int MaxMessages => ModEntry.Instance?.Config.MaxChatHistory ?? 100;
#pragma warning restore CA1822
    }

    private static ScrollState GetScrollState(ChatBox chatBox)
    {
        if (ScrollStates.TryGetValue(chatBox, out ScrollState? state)) return state;
        state = new ScrollState();
        ScrollStates[chatBox] = state;
        return state;
    }

    #region Max Messages Patch

    [HarmonyPatch(typeof(ChatBox), MethodType.Constructor)]
    public sealed class ConstructorPatch
    {
        private static void Postfix(ChatBox __instance)
        {
            ScrollState state = GetScrollState(__instance);
            __instance.maxMessages = state.MaxMessages;
        }
    }

    #endregion

    #region Scroll Wheel Handling

    [HarmonyPatch(typeof(ChatBox), "receiveScrollWheelAction")]
    public sealed class ReceiveScrollWheelActionPatch
    {
        private static bool Prefix(ChatBox __instance, int direction)
        {
            if (__instance.choosingEmoji)
                return true;

            if (!__instance.chatBox.Selected)
                return false;

            ScrollState state = GetScrollState(__instance);

            if (s_MessagesField.GetValue(__instance) is not List<ChatMessage> messages)
                return false;

            int totalHeight = CalculateTotalHeight(messages, true);
            int visibleHeight = GetVisibleHeight(__instance);
            int maxScroll = Math.Max(0, totalHeight - visibleHeight);

            // direction > 0 = scroll UP = see OLDER messages = INCREASE offset
            // direction < 0 = scroll DOWN = see NEWER messages = DECREASE offset
            float scrollAmount = direction > 0 ? 50f : -50f;
            state.TargetScrollOffset = Math.Clamp(state.TargetScrollOffset + scrollAmount, 0, maxScroll);

            return false;
        }
    }

    #endregion

    #region Update Patch

    [HarmonyPatch(typeof(ChatBox), "update", typeof(GameTime))]
    public sealed class UpdateScrollPatch
    {
        private static void Postfix(ChatBox __instance, GameTime time)
        {
            ScrollState state = GetScrollState(__instance);

            // Scroll smoothing
            if (Math.Abs(state.ScrollOffset - state.TargetScrollOffset) > 0.1f)
            {
                state.ScrollOffset = Utility.Lerp(state.ScrollOffset, state.TargetScrollOffset, 0.2f);
            }
            else
            {
                state.ScrollOffset = state.TargetScrollOffset;
            }

            // Update Color Picker Button ONLY when relevant state changes
            HandleColorPickerButton(__instance, state);
        }

        private static void HandleColorPickerButton(ChatBox chatBox, ScrollState state)
        {
            bool isColorButtonEnabled = ModEntry.Instance?.Config.EnableMessageColorButton ?? false;
            bool chatSelected = chatBox.chatBox.Selected;

            // Detect state changes
            bool enabledChanged = isColorButtonEnabled != state.WasColorButtonEnabled;
            bool selectionChanged = chatSelected != state.WasSelected;
            bool positionChanged = chatBox.chatBox.Width != state.LastChatBoxWidth ||
                                   chatBox.emojiMenuIcon.bounds.Y != state.LastEmojiIconY;

            // Only update if something changed
            if (enabledChanged || selectionChanged || positionChanged)
            {
                if (chatSelected && isColorButtonEnabled)
                {
                    // Create button if needed
                    if (_colorPickerButton == null)
                    {
                        _colorPickerButton = new ClickableTextureComponent(
                            new Rectangle(0, 0, 48, 48),
                            Game1.mouseCursors,
                            new Rectangle(119, 469, 16, 16),
                            3f)
                        {
                            hoverText = ModEntry.Instance?.Helper.Translation.Get("ui.colorPickerButton.tooltip") ?? "Message Color"
                        };
                    }

                    // Update position
                    _colorPickerButton.bounds.X = chatBox.xPositionOnScreen + chatBox.chatBox.Width + 8;
                    _colorPickerButton.bounds.Y = chatBox.emojiMenuIcon.bounds.Y - 6;

                    // Clamp to screen
                    if (_colorPickerButton.bounds.Bottom > Game1.uiViewport.Height)
                        _colorPickerButton.bounds.Y = Game1.uiViewport.Height - _colorPickerButton.bounds.Height;

                    // Cache position
                    state.LastChatBoxWidth = chatBox.chatBox.Width;
                    state.LastEmojiIconY = chatBox.emojiMenuIcon.bounds.Y;
                }
                else
                {
                    // Destroy button when disabled or chat closed
                    _colorPickerButton = null;
                }

                // Update cached state
                state.WasColorButtonEnabled = isColorButtonEnabled;
                state.WasSelected = chatSelected;
            }

            // Close color menu when chat closes (lightweight check)
            if (!chatSelected && _activeColorMenu != null)
            {
                _activeColorMenu.exitThisMenu();
                _activeColorMenu = null;
            }
        }
    }

    #endregion

    #region Position Patch

    [HarmonyPatch(typeof(ChatBox), "updatePosition")]
    public sealed class UpdatePositionPatch
    {
        private static void Postfix(ChatBox __instance)
        {
            // Force chatbox to bottom of screen (minus padding)
            // Original logic: yPositionOnScreen = Game1.uiViewport.Height - chatBox.Height;
            // Then logic modifies it. We want to strictly enforce bottom alignment if selected?
            // Or always?
            // User says "correct placement" is at bottom.
            // When inside, standard game logic might push it up if it thinks there's UI there.
            // We force it back down.

            // Re-calculate desired Y
            int desiredY = Game1.uiViewport.Height - __instance.chatBox.Height;

            // We only need to override Y.
            __instance.yPositionOnScreen = desiredY;
            __instance.chatBox.Y = desiredY;

            // Update children positions based on new Y
            __instance.emojiMenuIcon.bounds.Y = __instance.chatBox.Y + 8;
            if (__instance.emojiMenu != null)
            {
                __instance.emojiMenu.yPositionOnScreen = __instance.emojiMenuIcon.bounds.Y - 248;
            }
        }
    }

    #endregion

    #region Draw Patch

    [HarmonyPatch(typeof(ChatBox), "draw")]
    public sealed class DrawScrollPatch
    {
        private static bool Prefix(ChatBox __instance, SpriteBatch b)
        {
            ScrollState state = GetScrollState(__instance);

            if (s_MessagesField.GetValue(__instance) is not List<ChatMessage> messages)
                return true;


            if (__instance.chatBox.Selected)
            {
                // Calculate heights
                int totalHeight = CalculateTotalHeight(messages, true);
                int visibleHeight = GetVisibleHeight(__instance);
                // Clamp: with no messages this is 0, and a scissor rectangle of height
                // (displayHeight - 4) would be negative, which clips the whole chat away.
                int displayHeight = Math.Max(0, Math.Min(totalHeight, visibleHeight));

                // Draw background
                if (totalHeight > 0)
                {
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(301, 288, 15, 15),
                        __instance.xPositionOnScreen,
                        __instance.yPositionOnScreen - displayHeight - 20,
                        __instance.chatBox.Width,
                        displayHeight + 20,
                        Color.White, 4f, drawShadow: false);
                }

                // Nothing to clip or draw yet; skip straight to the text box.
                if (displayHeight <= 0)
                {
                    DrawChatBoxChrome(__instance, b);
                    return false;
                }

                // Set up clipping
                Rectangle oldScissor = b.GraphicsDevice.ScissorRectangle;
                RasterizerState? oldRaster = b.GraphicsDevice.RasterizerState;

                b.End();

                RasterizerState raster = new() { ScissorTestEnable = true };

                // The clipping area should be the visible message area
                Rectangle scissor = new(
                    __instance.xPositionOnScreen,
                    __instance.yPositionOnScreen - displayHeight - 8,
                    __instance.chatBox.Width,
                    Math.Max(1, displayHeight - 4)
                );

                b.GraphicsDevice.ScissorRectangle = scissor;
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, raster);

                // Draw messages using the same logic as the original game
                // Start from yPositionOnScreen and accumulate height going UP
                float heightSoFar = -state.ScrollOffset; // Apply scroll offset

                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    ChatMessage message = messages[i];

                    heightSoFar += message.verticalSize;

                    // Calculate Y position (same as original: y - heightSoFar - 8)
                    // Calculate Y position (same as original: y - heightSoFar - 8)
                    int drawY = __instance.yPositionOnScreen - (int)heightSoFar - 8;

                    // Check if message is visible in clipping region
                    int messageTop = drawY;
                    int messageBottom = drawY + message.verticalSize;

                    if (messageBottom >= scissor.Y - 10 && messageTop <= scissor.Y + scissor.Height + 10)
                    {
                        message.draw(b, __instance.xPositionOnScreen + 12, drawY);
                    }
                }

                // Draw scroll indicator
                if (totalHeight > visibleHeight)
                {
                    DrawScrollIndicator(b, __instance, state, totalHeight, visibleHeight, displayHeight);
                }

                b.End();
                b.GraphicsDevice.ScissorRectangle = oldScissor;
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, oldRaster);

                DrawChatBoxChrome(__instance, b);
            }
            else
            {
                // Chat closed - original behavior
                int heightSoFar = 0;
                bool drawBG = false;

                for (int j = messages.Count - 1; j >= 0; j--)
                {
                    if (messages[j].alpha > 0.01f)
                    {
                        heightSoFar += messages[j].verticalSize;
                        drawBG = true;
                    }
                }

                if (drawBG)
                {
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(301, 288, 15, 15),
                        __instance.xPositionOnScreen,
                        __instance.yPositionOnScreen - heightSoFar - 20 + __instance.chatBox.Height,
                        __instance.chatBox.Width,
                        heightSoFar + 20,
                        Color.White, 4f, drawShadow: false);
                }

                heightSoFar = 0;
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    ChatMessage message = messages[i];
                    if (message.alpha > 0.01f)
                    {
                        heightSoFar += message.verticalSize;
                        message.draw(b, __instance.xPositionOnScreen + 12,
                            __instance.yPositionOnScreen - heightSoFar - 8 + __instance.chatBox.Height);
                    }
                }
            }

            return false;
        }
    }

    #endregion


    #region Color Picker Input

    [HarmonyPatch(typeof(ChatBox), "receiveLeftClick")]
    public sealed class ReceiveLeftClickColorPatch
    {
        private static bool Prefix(ChatBox __instance, int x, int y)
        {
            if (!__instance.chatBox.Selected) return true;


            // Handle Button Click
            if (_colorPickerButton != null)
            {
                if (_colorPickerButton.containsPoint(x, y))
                {
                    Game1.playSound("drumkit6");

                    if (_activeColorMenu != null)
                    {
                        _activeColorMenu.exitThisMenu();
                        _activeColorMenu = null;
                    }
                    else
                    {
                        _activeColorMenu = new ColorPickerMenu(__instance, (_) =>
                        {
                            _activeColorMenu?.exitThisMenu(false);
                            _activeColorMenu = null;
                        });
                    }

                    return false;
                }
            }


            // Handle Menu Clicks
            if (_activeColorMenu != null)
            {
                if (_activeColorMenu.isWithinBounds(x, y))
                {
                    _activeColorMenu.receiveLeftClick(x, y);
                    return false; // Consume click
                }

                _activeColorMenu?.exitThisMenu();
                _activeColorMenu = null;
            }

            return true;
        }
    }

    #endregion

    #region isWithinBounds Patch

    [HarmonyPatch(typeof(ChatBox), "isWithinBounds")]
    public sealed class IsWithinBoundsPatch
    {
        private static void Postfix(ChatBox __instance, int x, int y, ref bool __result)
        {
            if (__result) return;

            // Check Color Picker Button
            if (_colorPickerButton != null)
            {
                if (_colorPickerButton.containsPoint(x, y))
                {
                    __result = true;
                    return;
                }
            }

            // Check Color Picker Menu
            if (_activeColorMenu != null)
            {
                if (_activeColorMenu.isWithinBounds(x, y))
                {
                    __result = true;
                    return;
                }
            }
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Draws the text input, emoji menu and colour picker. Shared so the chat box still
    ///     appears when there are no messages to clip and draw.
    /// </summary>
    private static void DrawChatBoxChrome(ChatBox chatBox, SpriteBatch b)
    {
        // Draw text input and emoji menu
        chatBox.chatBox.Draw(b, drawShadow: false);
        chatBox.emojiMenuIcon.draw(b, Color.White, 0.99f);

        if (chatBox.choosingEmoji)
        {
            chatBox.emojiMenu.draw(b);
        }

        // Draw Color Picker Button
        if (_colorPickerButton != null)
        {
            _colorPickerButton.tryHover(Game1.getMouseX(), Game1.getMouseY());

            // Tint button with current color -> Disabled by user request
            _colorPickerButton.draw(b, Color.White, 0.99f);

            if (_colorPickerButton.containsPoint(Game1.getMouseX(), Game1.getMouseY()))
            {
                IClickableMenu.drawHoverText(b, _colorPickerButton.hoverText, Game1.smallFont);
            }
        }

        // Draw Color Picker Menu
        if (_activeColorMenu != null)
        {
            _activeColorMenu.draw(b);
        }

        if (chatBox.isWithinBounds(Game1.getMouseX(), Game1.getMouseY()) && !Game1.options.hardwareCursor)
        {
            Game1.mouseCursor = Game1.options.gamepadControls
                ? Game1.cursor_gamepad_pointer
                : Game1.cursor_default;
        }
    }

    private static int CalculateTotalHeight(List<ChatMessage> messages, bool chatSelected)
    {
        int height = 0;
        foreach (ChatMessage message in messages)
        {
            if (chatSelected || message.alpha > 0.01f)
            {
                height += message.verticalSize;
            }
        }

        return height;
    }

    private static int GetVisibleHeight(ChatBox chatBox)
    {
        // Leave space for the text box and reasonable padding
        int screenHeight = Game1.uiViewport.Height;
        int chatBoxHeight = chatBox.chatBox.Height;

        // Available space: screen height minus text box minus padding
        int availableHeight = screenHeight - chatBoxHeight - 150;

        // Cap at reasonable max
        return Math.Min(availableHeight, 400);
    }

    private static void DrawScrollIndicator(SpriteBatch b, ChatBox chatBox, ScrollState state,
        int totalHeight, int visibleHeight, int displayHeight)
    {
        int scrollBarX = chatBox.xPositionOnScreen + chatBox.chatBox.Width - 9;
        int scrollBarY = chatBox.yPositionOnScreen - displayHeight - 12;
        int scrollBarHeight = displayHeight;

        // Background
        b.Draw(Game1.staminaRect,
            new Rectangle(scrollBarX, scrollBarY, 6, scrollBarHeight),
            new Color(0, 0, 0, 80));

        // Thumb - inverted so it starts at bottom and moves up
        float scrollableHeight = totalHeight - visibleHeight;
        float scrollPercentage = state.ScrollOffset / scrollableHeight;
        float thumbHeightRatio = (float)visibleHeight / totalHeight;
        float thumbHeight = Math.Max(20, scrollBarHeight * thumbHeightRatio);
        // Invert: when offset=0 (newest), thumb at bottom. When offset=max (oldest), thumb at top
        float thumbY = scrollBarY + (scrollBarHeight - thumbHeight) * (1f - scrollPercentage);

        b.Draw(Game1.staminaRect,
            new Rectangle(scrollBarX, (int)thumbY, 6, (int)thumbHeight),
            new Color(255, 255, 255, 180));

        // Arrows - show when there's more content in that direction
        SpriteFont font = Game1.smallFont;

        if (state.ScrollOffset < scrollableHeight - 1)
        {
            // Can scroll up to see older messages - show up arrow
            string upText = "↑";
            Vector2 upSize = font.MeasureString(upText);
            b.DrawString(font, upText,
                new Vector2(scrollBarX - upSize.X / 2 + 3, scrollBarY - upSize.Y - 2),
                Color.White * 0.8f);
        }

        if (state.ScrollOffset > 1)
        {
            // Can scroll down to see newer messages - show down arrow
            string downText = "↓";
            Vector2 downSize = font.MeasureString(downText);
            b.DrawString(font, downText,
                new Vector2(scrollBarX - downSize.X / 2 + 3, scrollBarY + scrollBarHeight + 2),
                Color.White * 0.8f);
        }
    }

    #endregion

    #region Message Height Fix

    /// <summary>
    /// Fix message height calculation for multi-line messages
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), "receiveChatMessage")]
    public sealed class ReceiveChatMessagePatch
    {
        private static void Prefix(ChatBox __instance)
        {
            ScrollState state = GetScrollState(__instance);
            __instance.maxMessages = state.MaxMessages;
        }

        private static void Postfix(ChatBox __instance)
        {
            // Get the messages list
            if (s_MessagesField.GetValue(__instance) is not List<ChatMessage> messages || messages.Count == 0)
                return;

            // Fix the height of the most recently added message
            ChatMessage lastMessage = messages[messages.Count - 1];
            FixMessageHeight(lastMessage, __instance.chatBox.Width);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return SharedTranspilerLogic(instructions);
        }
    }

    /// <summary>
    /// Fix message height calculation for addMessage
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), "addMessage")]
    public sealed class AddMessagePatch
    {
        private static void Postfix(ChatBox __instance)
        {
            // Get the messages list
            if (s_MessagesField.GetValue(__instance) is not List<ChatMessage> messages || messages.Count == 0)
                return;

            // Fix the height of the most recently added message
            ChatMessage lastMessage = messages[messages.Count - 1];
            FixMessageHeight(lastMessage, __instance.chatBox.Width);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return SharedTranspilerLogic(instructions);
        }
    }

    private static IEnumerable<CodeInstruction> SharedTranspilerLogic(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo parseTextMethod = AccessTools.Method(typeof(Game1), nameof(Game1.parseText),
            new[] { typeof(string), typeof(SpriteFont), typeof(int) });

        MethodInfo customParseMethod =
            AccessTools.Method(typeof(TextHelper), nameof(TextHelper.ParseTextWithWrapping));

        foreach (var instruction in instructions)
        {
            if (instruction.Calls(parseTextMethod))
            {
                yield return new CodeInstruction(OpCodes.Call, customParseMethod);
            }
            else
            {
                yield return instruction;
            }
        }
    }



    private static void FixMessageHeight(ChatMessage message, int chatBoxWidth)
    {
        // Count the actual lines using the same logic as the draw method
        int lineCount = TextHelper.CountMessageLines(message);

        // Each line needs space based on font measurement
        // Use the same measurement the game uses
        SpriteFont font = ChatBox.messageFont(message.language);
        if (font == null)
        {
            message.verticalSize = 40; // Fallback
            return;
        }

        // The game uses MeasureString("(").Y for line height
        float lineHeight = font.MeasureString("(").Y;
        message.verticalSize = (int)(lineCount * lineHeight) + 12;
    }

    #endregion
}
