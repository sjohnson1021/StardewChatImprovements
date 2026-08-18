using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

public class ColorPickerMenu : IClickableMenu
{
    public List<ClickableTextureComponent> Icons { get; } = new();
    public List<Color> IconColors { get; } = new();
    public List<string> ColorNames { get; } = new();
    private int _selected = -1;
    private readonly Action<string>? _onSelection;

    private static readonly string[] AvailableColors =
    {
        "white", "red", "blue", "green", "jade", "yellowgreen", "jungle",
        "yellow", "orange", "brown", "cream", "peach",
        "purple", "pink", "plum", "salmon", "aqua", "gray"
    };

    public ColorPickerMenu(ChatBox chatBox, Action<string>? onSelection = null)
    {
        _onSelection = onSelection;
        Game1.activeClickableMenu = this;
        SetUpIcons(chatBox);
    }

    // We need to re-setup if window size changes, but we need reference to chatBox/button position.
    // For simplicity, we just close it on resize or let it be. 
    // But setUpIcons needs position. We'll store chatBox reference.
    private ChatBox _chatBox = null!; // Initialized in SetUpIcons called by Ctor

    public void SetUpIcons(ChatBox chatBox)
    {
        _chatBox = chatBox;

        int iconSize = 28; // 28x28 pixels
        int iconSpacing = 4; // Smaller spacing
        int border = 16;

        Icons.Clear();
        IconColors.Clear();
        ColorNames.Clear();
        // Just draw a colored rect and a border.

        string currentColor = Game1.player.defaultChatColor ?? "white";

        for (int i = 0; i < AvailableColors.Length; i++)
        {
            string name = AvailableColors[i];
            Color c = ChatMessage.getColorFromName(name);

            // Bounds will be set later
            Icons.Add(new ClickableTextureComponent(name, new Rectangle(0, 0, iconSize, iconSize), null, ModEntry.Instance?.Helper.Translation.Get("color." + name) ?? name, null, Rectangle.Empty, 1f));

            IconColors.Add(c);
            ColorNames.Add(name);

            if (name == currentColor)
            {
                _selected = i;
            }
        }

        // Layout: 3 Rows x 6 Cols
        int cols = 6;
        int rows = 3;

        int contentWidth = cols * iconSize + (cols - 1) * iconSpacing;
        int contentHeight = rows * iconSize + (rows - 1) * iconSpacing;

        width = contentWidth + border * 2;
        height = contentHeight + border * 2;

        // Position: Above the button.
        // We need button position.
        // ChatBoxPatches.ColorPickerButtons stores it.
        // Since we are inside ChatImprovements, we can access it maybe?
        // Or we pass it.
        // Let's assume the button is at the standard position relative to ChatBox.
        // Button X: chatBox.X + chatBox.Width + 8
        // Button Y: emojiIcon.Y + ...

        // Easier: ChatBoxPatches can set our position.
        // But we want to self-manage.

        // Let's calculate based on known offsets.
        // ChatBox is at bottom.
        // Button is to right of ChatBox.

        // Target: Right aligned with button? Or Centered on button?
        // Button X ~ chatBox.xPositionOnScreen + chatBox.Width + 8
        // Let's rely on chatBox properties.

        int btnX = chatBox.xPositionOnScreen + chatBox.chatBox.Width + 8;
        int btnY = chatBox.emojiMenuIcon.bounds.Y; // Approx

        // Place menu above button
        xPositionOnScreen = btnX + 48 / 2 - width / 2; // Center on 48px button
        yPositionOnScreen = btnY - height - 8;

        // Ensure within screen
        if (xPositionOnScreen + width > Game1.uiViewport.Width)
            xPositionOnScreen = Game1.uiViewport.Width - width - 4;

        if (yPositionOnScreen < 0)
            yPositionOnScreen = btnY + 48 + 8; // Flip to below if no space above

        int startX = xPositionOnScreen + border;
        int startY = yPositionOnScreen + border;

        for (int i = 0; i < Icons.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;

            Icons[i].bounds.X = startX + col * (iconSize + iconSpacing);
            Icons[i].bounds.Y = startY + row * (iconSize + iconSpacing);
        }

        initialize(xPositionOnScreen, yPositionOnScreen, width, height, showUpperRightCloseButton: false);
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        SetUpIcons(_chatBox);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {

        for (int i = 0; i < Icons.Count; i++)
        {
            if (Icons[i].containsPoint(x, y))
            {
                Game1.playSound("coin");
                Game1.player.defaultChatColor = ColorNames[i];
                _selected = i;
                _onSelection?.Invoke(ColorNames[i]);
                return;
            }
        }

        if (!isWithinBounds(x, y))
        {
            _chatBox.receiveLeftClick(Game1.getMouseX(true), Game1.getMouseY(true));
        }
    }

    public override void draw(SpriteBatch b)
    {
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

        if (upperRightCloseButton != null)
            upperRightCloseButton.draw(b);

        bool hoveringIcon = false;
        string? hoverText = null;

        for (int i = 0; i < Icons.Count; i++)
        {
            // Just draw a colored rect and a border.
            // Draw background for cell (optional, maybe dark rect?)
            b.Draw(Game1.staminaRect, Icons[i].bounds, new Color(60, 60, 60)); // Dark grey back

            // Draw color swatch
            Rectangle swatchRect = Icons[i].bounds;
            swatchRect.Inflate(-2, -2); // Border of 2px
            b.Draw(Game1.staminaRect, swatchRect, IconColors[i]);

            if (_selected == i)
            {
                // Draw selection border
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(375, 357, 3, 3),
                    Icons[i].bounds.X - 2, Icons[i].bounds.Y - 2, Icons[i].bounds.Width + 2, Icons[i].bounds.Height + 2,
                    Color.White, 4f, false); // Where to change selection border/color
            }
            else if (Icons[i].containsPoint(Game1.getMouseX(), Game1.getMouseY()))
            {
                // Hover effect (highlight)
                b.Draw(Game1.staminaRect, Icons[i].bounds, Color.White * 0.2f);
                hoveringIcon = true;
                hoverText = Icons[i].hoverText;
            }
        }

        if (isWithinBounds(Game1.getMouseX(), Game1.getMouseY()))
        {
            Game1.mouseCursor = hoveringIcon switch
            {
                true => Game1.cursor_gamepad_pointer,
                _ => Game1.cursor_default
            };
        }

        if (hoverText != null)
        {
            IClickableMenu.drawHoverText(b, hoverText, Game1.smallFont);
        }

        drawMouse(b, true, Game1.mouseCursor);
    }
    public override bool isWithinBounds(int x, int y)
    {
        return x >= xPositionOnScreen && x <= xPositionOnScreen + width &&
               y >= yPositionOnScreen && y <= yPositionOnScreen + height;
    }
}
