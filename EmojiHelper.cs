using System.Text.RegularExpressions;

namespace ChatImprovements;

/// <summary>
/// Helper for handling Stardew Valley's emoji codes (e.g. [123]).
/// </summary>
internal static class EmojiHelper
{
    private static readonly Regex EmojiRegex = new(@"\[\d{1,3}\]", RegexOptions.Compiled);

    /// <summary>
    /// Snaps cursor to emoji boundaries to prevent splitting emoji codes.
    /// </summary>
    /// <param name="text">The full text content.</param>
    /// <param name="cursor">The proposed cursor position.</param>
    /// <param name="direction">
    /// -1 to snap to start of emoji (moving left)
    /// 1 to snap to end of emoji (moving right)
    /// 0 to snap to nearest boundary
    /// </param>
    /// <returns>Adjusted cursor position.</returns>
    public static int SnapToBoundary(string text, int cursor, int direction)
    {
        if (string.IsNullOrEmpty(text) || cursor < 0 || cursor > text.Length)
            return cursor;

        foreach (Match match in EmojiRegex.Matches(text))
        {
            int start = match.Index;
            int end = match.Index + match.Length;

            // Cursor is inside an emoji code
            if (cursor > start && cursor < end)
            {
                if (direction < 0) return start;
                if (direction > 0) return end;
                // Snap to nearest
                return (cursor - start < end - cursor) ? start : end;
            }
        }

        return cursor;
    }

    /// <summary>
    /// Identifies the emoji range to delete at the current position.
    /// </summary>
    /// <param name="text">The full text content.</param>
    /// <param name="pos">The cursor position.</param>
    /// <param name="isBackspace">True if deleting backwards (Backspace), false if forwards (Delete).</param>
    /// <returns>Tuple of (start, end) indices, or (-1, -1) if no emoji found.</returns>
    public static (int start, int end) GetEmojiRange(string text, int pos, bool isBackspace)
    {
        if (string.IsNullOrEmpty(text) || pos < 0 || pos > text.Length)
            return (-1, -1);

        foreach (Match match in EmojiRegex.Matches(text))
        {
            int start = match.Index;
            int end = match.Index + match.Length;

            if (isBackspace)
            {
                // Backspace: cursor is at end of emoji or inside it?
                // Usually Backspace removes the char BEFORE the cursor.
                // So if cursor is at 'end', we are deleting the emoji ending at 'end'.
                if (pos > start && pos <= end)
                    return (start, end);
            }
            else
            {
                // Delete: cursor is at start of emoji
                if (pos >= start && pos < end)
                    return (start, end);
            }
        }

        return (-1, -1);
    }
}
