using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System.Runtime.CompilerServices;

namespace ChatImprovements;

#pragma warning disable CS8602
internal class ChatBoxScrollPatches
{
    private static readonly Dictionary<ChatBox, ScrollState> ScrollStates = new();

    // Color Picker State
    public static readonly ConditionalWeakTable<ChatBox, ClickableTextureComponent> ColorPickerButtons = new();
    public static readonly ConditionalWeakTable<ChatBox, ColorPickerMenu> ColorPickerMenus = new();

    private class ScrollState
    {
        public float ScrollOffset;
        public float TargetScrollOffset;
        public bool WasSelected;

        public int MaxMessages => ModEntry.Instance?.Config.MaxChatHistory ?? 100;
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
    public class ConstructorPatch
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
    public class ReceiveScrollWheelActionPatch
    {
        private static bool Prefix(ChatBox __instance, int direction)
        {
            if (__instance.choosingEmoji)
                return true;

            if (!__instance.chatBox.Selected)
                return false;

            ScrollState state = GetScrollState(__instance);

            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField?.GetValue(__instance) is not List<ChatMessage> messages)
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
    public class UpdateScrollPatch
    {
        private static void Postfix(ChatBox __instance, GameTime time)
        {
            ScrollState state = GetScrollState(__instance);

            // When chat is first opened, fix heights of all existing messages
            if (__instance.chatBox.Selected && !state.WasSelected)
            {
                FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
                if (messagesField?.GetValue(__instance) is List<ChatMessage> messages)
                {
                    foreach (ChatMessage msg in messages)
                    {
                        FixMessageHeight(msg, __instance.chatBox.Width);
                    }
                }

                state.ScrollOffset = 0;
                state.TargetScrollOffset = 0;
            }

            state.WasSelected = __instance.chatBox.Selected;

            if (Math.Abs(state.ScrollOffset - state.TargetScrollOffset) > 0.5f)
            {
                float delta = state.TargetScrollOffset - state.ScrollOffset;
                state.ScrollOffset += delta * 0.25f;
            }
            else
            {
                state.ScrollOffset = state.TargetScrollOffset;
            }

            FieldInfo? messagesField2 = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField2?.GetValue(__instance) is not List<ChatMessage> messages2)
                return;

            int totalHeight = CalculateTotalHeight(messages2, __instance.chatBox.Selected);
            int visibleHeight = GetVisibleHeight(__instance);
            int maxScroll = Math.Max(0, totalHeight - visibleHeight);

            state.ScrollOffset = Math.Clamp(state.ScrollOffset, 0, maxScroll);
            state.TargetScrollOffset = Math.Clamp(state.TargetScrollOffset, 0, maxScroll);

            // Update Color Picker Button
            if (__instance.chatBox.Selected)
            {
                if (!ColorPickerButtons.TryGetValue(__instance, out var colorButton))
                {
                    colorButton = new ClickableTextureComponent(
                        new Rectangle(0, 0, 48, 48),
                        Game1.mouseCursors,
                        new Rectangle(119, 469, 16, 16),
                        3f)
                    {
                        hoverText = "Message Color"
                    };
                    ColorPickerButtons.AddOrUpdate(__instance, colorButton);
                }


                // Position to the right of the chatbox
                colorButton.bounds.X = __instance.xPositionOnScreen + __instance.chatBox.Width + 8;
                // Center vertically with emoji button (36px high)
                // Our button is 16*3 = 48px high.
                // emojiMenuIcon.bounds.Y is chatBox.Y + 8.
                // Center: emojiY + (36 - 48)/2 = emojiY - 6
                colorButton.bounds.Y = __instance.emojiMenuIcon.bounds.Y - 6;

                // Ensure button doesn't go offscreen
                if (colorButton.bounds.Bottom > Game1.uiViewport.Height)
                    colorButton.bounds.Y = Game1.uiViewport.Height - colorButton.bounds.Height;

                // Update Menu if open
                if (ColorPickerMenus.TryGetValue(__instance, out var menu))
                {
                    // Keep menu centered or positioned?
                    // It's already positioned in Constructor.
                }
            }
        }
    }

    #endregion

    #region Position Patch

    #region Position Patch

    [HarmonyPatch(typeof(ChatBox), "updatePosition")]
    public class UpdatePositionPatch
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

    #endregion

    #region Draw Patch

    [HarmonyPatch(typeof(ChatBox), "draw")]
    public class DrawScrollPatch
    {
        private static bool Prefix(ChatBox __instance, SpriteBatch b)
        {
            ScrollState state = GetScrollState(__instance);

            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField?.GetValue(__instance) is not List<ChatMessage> messages)
                return true;

            if (__instance.chatBox.Selected)
            {
                // Calculate heights
                int totalHeight = CalculateTotalHeight(messages, true);
                int visibleHeight = GetVisibleHeight(__instance);
                int displayHeight = Math.Min(totalHeight, visibleHeight);

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
                    displayHeight - 4
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

                // Draw text input and emoji menu
                __instance.chatBox.Draw(b, drawShadow: false);
                __instance.emojiMenuIcon.draw(b, Color.White, 0.99f);

                if (__instance.choosingEmoji)
                {
                    __instance.emojiMenu.draw(b);
                }

                // Draw Color Picker Button
                if (ColorPickerButtons.TryGetValue(__instance, out var colorButton))
                {
                    colorButton.tryHover(Game1.getMouseX(), Game1.getMouseY());

                    // Tint button with current color -> Disabled by user request
                    colorButton.draw(b, Color.White, 0.99f);

                    if (colorButton.containsPoint(Game1.getMouseX(), Game1.getMouseY()))
                    {
                        IClickableMenu.drawHoverText(b, colorButton.hoverText, Game1.smallFont);
                    }
                }

                // Draw Color Picker Menu
                if (ColorPickerMenus.TryGetValue(__instance, out var menu))
                {
                    menu.draw(b);
                }

                if (__instance.isWithinBounds(Game1.getMouseX(), Game1.getMouseY()) && !Game1.options.hardwareCursor)
                {
                    Game1.mouseCursor = Game1.options.gamepadControls
                        ? Game1.cursor_gamepad_pointer
                        : Game1.cursor_default;
                }
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
    public class ReceiveLeftClickColorPatch
    {
        private static bool Prefix(ChatBox __instance, int x, int y)
        {
            if (!__instance.chatBox.Selected) return true;

            // Handle Menu Clicks
            if (ColorPickerMenus.TryGetValue(__instance, out var menu))
            {
                if (menu.isWithinBounds(x, y))
                {
                    menu.receiveLeftClick(x, y);

                    // If we clicked an option, close menu?
                    // The menu handles color selection.
                    // We can check if we should close.
                    // For now, let's close on any click inside boundaries if it wasn't a close button?
                    // Actually, let's just let the menu logic run.
                    return false; // Consume click
                }
            }

            // Handle Button Click
            if (ColorPickerButtons.TryGetValue(__instance, out var colorButton))
            {
                if (colorButton.containsPoint(x, y))
                {
                    Game1.playSound("drumkit6");

                    if (ColorPickerMenus.TryGetValue(__instance, out var existingMenu))
                    {
                        ColorPickerMenus.Remove(__instance);
                    }
                    else
                    {
                        ColorPickerMenus.AddOrUpdate(__instance,
                            new ColorPickerMenu(__instance, (_) => ColorPickerMenus.Remove(__instance)));
                    }

                    return false;
                }
            }

            // Close menu if clicked outside
            if (ColorPickerMenus.TryGetValue(__instance, out var _))
            {
                // If we reached here, we clicked outside the menu (and outside the button)
                ColorPickerMenus.Remove(__instance);
                // Don't return false, let others handle the click (e.g. typing or emoji)
            }

            return true;
        }
    }

    #endregion

    #region isWithinBounds Patch

    [HarmonyPatch(typeof(ChatBox), "isWithinBounds")]
    public class IsWithinBoundsPatch
    {
        private static void Postfix(ChatBox __instance, int x, int y, ref bool __result)
        {
            if (__result) return;

            // Check Color Picker Button
            if (ColorPickerButtons.TryGetValue(__instance, out var colorButton))
            {
                if (colorButton.containsPoint(x, y))
                {
                    __result = true;
                    return;
                }
            }

            // Check Color Picker Menu
            if (ColorPickerMenus.TryGetValue(__instance, out var menu))
            {
                if (menu.isWithinBounds(x, y))
                {
                    __result = true;
                    return;
                }
            }
        }
    }

    #endregion

    #region Helper Methods

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
    public class ReceiveChatMessagePatch
    {
        private static void Prefix(ChatBox __instance)
        {
            ScrollState state = GetScrollState(__instance);
            __instance.maxMessages = state.MaxMessages;
        }

        private static void Postfix(ChatBox __instance)
        {
            // Get the messages list
            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField?.GetValue(__instance) is not List<ChatMessage> messages || messages.Count == 0)
                return;

            // Fix the height of the most recently added message
            // Fix the height of the most recently added message
            ChatMessage lastMessage = messages[messages.Count - 1];
            FixMessageHeight(lastMessage, __instance.chatBox.Width);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo parseTextMethod = AccessTools.Method(typeof(Game1), nameof(Game1.parseText),
                new[] { typeof(string), typeof(SpriteFont), typeof(int) });
            MethodInfo customParseMethod =
                AccessTools.Method(typeof(ChatBoxScrollPatches), nameof(ParseTextWithWrapping));

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
    }

    /// <summary>
    /// Fix message height calculation for addMessage
    /// </summary>
    [HarmonyPatch(typeof(ChatBox), "addMessage")]
    public class AddMessagePatch
    {
        private static void Postfix(ChatBox __instance)
        {
            // Get the messages list
            FieldInfo? messagesField = AccessTools.Field(typeof(ChatBox), "messages");
            if (messagesField?.GetValue(__instance) is not List<ChatMessage> messages || messages.Count == 0)
                return;

            // Fix the height of the most recently added message
            ChatMessage lastMessage = messages[messages.Count - 1];
            FixMessageHeight(lastMessage, __instance.chatBox.Width);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo parseTextMethod = AccessTools.Method(typeof(Game1), nameof(Game1.parseText),
                new[] { typeof(string), typeof(SpriteFont), typeof(int) });
            MethodInfo customParseMethod =
                AccessTools.Method(typeof(ChatBoxScrollPatches), nameof(ParseTextWithWrapping));

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
    }

    public static string ParseTextWithWrapping(string text, SpriteFont font, int width)
    {
        // Reduce width slightly more to prevent overlap with the right border
        // User reported overlap up to 8px, so we remove an extra 12px for safety
        width += 14;

        if (string.IsNullOrEmpty(text))
            return "";

        System.Text.StringBuilder result = new();
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.AppendLine();

            string currentLine = lines[i];
            if (string.IsNullOrEmpty(currentLine)) continue;

            string[] words = currentLine.Split(' ');
            float currentLineWidth = 0f;
            float spaceWidth = font.MeasureString(" ").X;
            bool firstWordInLine = true;

            foreach (string word in words)
            {
                float wordWidth = font.MeasureString(word).X;

                // Handle words that are longer than the entire width by themselves
                if (wordWidth > width)
                {
                    // If we have content on the current line, wrap first
                    if (!firstWordInLine)
                    {
                        result.AppendLine();
                        currentLineWidth = 0f;
                        firstWordInLine = true;
                    }

                    // Split the long word
                    string remainingWord = word;
                    while (font.MeasureString(remainingWord).X > width)
                    {
                        // Find the split point
                        int splitIndex = 0;
                        float partialWidth = 0f;
                        for (int k = 0; k < remainingWord.Length; k++)
                        {
                            float charWidth = font.MeasureString(remainingWord[k].ToString()).X;
                            if (partialWidth + charWidth > width)
                            {
                                break;
                            }

                            partialWidth += charWidth;
                            splitIndex++;
                        }

                        // Safety check to ensure we make progress
                        if (splitIndex == 0) splitIndex = 1;

                        result.Append(remainingWord.Substring(0, splitIndex));
                        result.AppendLine();
                        remainingWord = remainingWord.Substring(splitIndex);
                    }

                    // Append the remainder of the long word
                    result.Append(remainingWord);
                    currentLineWidth = font.MeasureString(remainingWord).X;
                    firstWordInLine = false;

                    // Add space after this word if it's not the last
                    // (The loop logic below normally adds space before, but since we just handled a word...
                    // actually, simple logic: we are consuming 'word' from the split list.
                    // The standard loop logic appends space if !firstWordInLine.
                    // We just finished a word. The NEXT word will trigger the space add.)
                    continue;
                }

                // Normal word handling
                if (!firstWordInLine)
                {
                    if (currentLineWidth + spaceWidth + wordWidth > width)
                    {
                        result.AppendLine();
                        currentLineWidth = 0f;
                        firstWordInLine = true;
                    }
                    else
                    {
                        result.Append(" ");
                        currentLineWidth += spaceWidth;
                    }
                }

                result.Append(word);
                currentLineWidth += wordWidth;
                firstWordInLine = false;
            }
        }

        return result.ToString();
    }

    //Likely need to patch addMessage and receiveChatMessage as this is where we call the parseText method with a specified width (chatboxWidth - 8), we need to adjust this and lower the width by an additional 8 to account for the padding
    //Here is the base code from the decompiled game:
    /*
     *
     *
        public virtual void receiveChatMessage(long sourceFarmer, int chatKind, LocalizedContentManager.LanguageCode language, string message)
        {
            string text = formatMessage(sourceFarmer, chatKind, message);
            ChatMessage c = new ChatMessage();
            string s = Game1.parseText(text, chatBox.Font, chatBox.Width - 16);
            c.timeLeftToDisplay = 600;
            c.verticalSize = (int)chatBox.Font.MeasureString(s).Y + 4;
            c.color = messageColor(chatKind);
            c.language = language;
            c.parseMessageForEmoji(s);
            messages.Add(c);
            if (messages.Count > maxMessages)
            {
                messages.RemoveAt(0);
            }
            if (chatKind == 3 && sourceFarmer != Game1.player.UniqueMultiplayerID)
            {
                lastReceivedPrivateMessagePlayerId = sourceFarmer;
            }
        }

        public virtual void addMessage(string message, Color color)
        {
            ChatMessage c = new ChatMessage();
            string s = Game1.parseText(message, chatBox.Font, chatBox.Width - 8);
            c.timeLeftToDisplay = 600;
            c.verticalSize = (int)chatBox.Font.MeasureString(s).Y + 4;
            c.color = color;
            c.language = LocalizedContentManager.CurrentLanguageCode;
            c.parseMessageForEmoji(s);
            messages.Add(c);
            if (messages.Count > maxMessages)
            {
                messages.RemoveAt(0);
            }
        }
     *
     */


    private static void FixMessageHeight(ChatMessage message, int chatBoxWidth)
    {
        // Count the actual lines using the same logic as the draw method
        int lineCount = CountMessageLines(message);

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

    private static int CountMessageLines(ChatMessage message)
    {
        if (message.message == null || message.message.Count == 0)
            return 1;

        // Replicate the EXACT logic from ChatMessage.draw()
        float xPositionSoFar = 0f;
        int lineCount = 1; // Start with 1 line
        const float maxLineWidth = 860f;

        for (int i = 0; i < message.message.Count; i++)
        {
            ChatSnippet snippet = message.message[i];

            if (snippet.emojiIndex != -1)
            {
                // Emoji - just add its width
                xPositionSoFar += snippet.myLength;
            }
            else if (snippet.message != null)
            {
                if (snippet.message.Equals(Environment.NewLine))
                {
                    // Explicit newline - reset x position and add line
                    xPositionSoFar = 0f;
                    lineCount++;
                }
                else
                {
                    // Regular text - add its width
                    xPositionSoFar += snippet.myLength;
                }
            }

            // Check for wrapping (same as draw method)
            if (xPositionSoFar >= maxLineWidth)
            {
                xPositionSoFar = 0f;
                lineCount++;

                // If next snippet is a newline, skip it (same as draw method)
                if (i + 1 < message.message.Count &&
                    message.message[i + 1].message != null &&
                    message.message[i + 1].message.Equals(Environment.NewLine))
                {
                    i++;
                }
            }
        }

        return lineCount;
    }

    #endregion
}
#pragma warning restore CS8602