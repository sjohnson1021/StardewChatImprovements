using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

public class ColorPickerMenu : IClickableMenu
{
    public List<ClickableTextureComponent> Icons = new List<ClickableTextureComponent>();
    public List<Color> IconColors = new List<Color>();
    public List<string> ColorNames = new List<string>();

    private int _selected = -1;
    private Action<string> _onSelection;

    private static readonly string[] AvailableColors =
    {
        "white", "red", "blue", "green", "jade", "yellowgreen", "jungle",
        "yellow", "orange", "brown", "cream", "peach",
        "purple", "pink", "plum", "salmon", "aqua", "gray"
    };

    public ColorPickerMenu(ChatBox chatBox, Action<string> onSelection = null)
    {
        _onSelection = onSelection;
        SetUpIcons(chatBox);
    }

    // We need to re-setup if window size changes, but we need reference to chatBox/button position.
    // For simplicity, we just close it on resize or let it be. 
    // But setUpIcons needs position. We'll store chatBox reference.
    private ChatBox _chatBox;

    public void SetUpIcons(ChatBox chatBox)
    {
        _chatBox = chatBox;

        int iconSize = 28; // 28x28 pixels
        int iconSpacing = 4; // Smaller spacing
        int border = 16;

        Icons.Clear();
        IconColors.Clear();
        ColorNames.Clear();
        // Standard slot background or just drawn box?
        // We will draw cells manually or use a simple box.
        // Let's use standard slotSource but scaled? No, slot source is 24x24.
        // We can just draw a colored rect and a border.

        string currentColor = Game1.player.defaultChatColor ?? "white";

        for (int i = 0; i < AvailableColors.Length; i++)
        {
            string name = AvailableColors[i];
            Color c = ChatMessage.getColorFromName(name);

            // Bounds will be set later
            Icons.Add(new ClickableTextureComponent(new Rectangle(0, 0, iconSize, iconSize), null, Rectangle.Empty, 1f)
            {
                name = name,
                hoverText = name
            });

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

        initialize(xPositionOnScreen, yPositionOnScreen, width, height, showUpperRightCloseButton: true);
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        SetUpIcons(_chatBox);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
        {
            if (playSound) Game1.playSound("bigDeSelect");
            // Close logic handled by owner?
            // We need a way to close. 
            // We can invoke selection with null or just expect owner to check isWithinBounds?
            // Actually, we can just do nothing and let the 'click outside' logic handle it?
            // Or we should callback.
            // But we don't have a close callback.
            // Let's assume standard behavior: clicking close button does nothing unless we handle it?
            // Actually, clicking close button usually closes.
            // We will invoke selection with current color to close?
            // Or force close.
            _onSelection?.Invoke(null); // Signal close without change?
            return;
        }

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
    }

    public override void draw(SpriteBatch b)
    {
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

        if (upperRightCloseButton != null)
            upperRightCloseButton.draw(b);

        for (int i = 0; i < Icons.Count; i++)
        {
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
                    Icons[i].bounds.X, Icons[i].bounds.Y, Icons[i].bounds.Width, Icons[i].bounds.Height,
                    Color.White, 4f, false);
            }
            else if (Icons[i].containsPoint(Game1.getMouseX(), Game1.getMouseY()))
            {
                // Hover effect (highlight)
                b.Draw(Game1.staminaRect, Icons[i].bounds, Color.White * 0.2f);
            }
        }

        drawMouse(b);
    }

    public override bool isWithinBounds(int x, int y)
    {
        return x >= xPositionOnScreen && x <= xPositionOnScreen + width &&
               y >= yPositionOnScreen && y <= yPositionOnScreen + height;
    }
}
