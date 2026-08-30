using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

public static class TextHelper
{
    private const float MaxLineWidth = 872f;

    /// <summary>Marker Item Chat Link wraps around the data behind a linked item.</summary>
    private const string ItemLinkOpen = "{icl:v1|";

    /// <summary>
    ///     Index of the first grapheme cluster boundary after <paramref name="index" />.
    /// </summary>
    /// <remarks>
    ///     Stepping by one <see cref="char" /> splits surrogate pairs and separates combining marks
    ///     from their base character, corrupting text in Korean, Hindi, Vietnamese and emoji
    ///     sequences. A family emoji is 11 chars but one character to the player.
    /// </remarks>
    public static int NextGrapheme(string text, int index)
    {
        if (index >= text.Length) return text.Length;
        return index + StringInfo.GetNextTextElementLength(text.AsSpan(index));
    }

    /// <summary>Index of the last grapheme cluster boundary before <paramref name="index" />.</summary>
    public static int PrevGrapheme(string text, int index)
    {
        if (index <= 0) return 0;

        int pos = 0;
        while (pos < text.Length)
        {
            int next = pos + StringInfo.GetNextTextElementLength(text.AsSpan(pos));
            if (next >= index) return pos;
            pos = next;
        }

        return pos;
    }

    /// <summary>
    ///     Stands in for vanilla's <see cref="Game1.parseText" /> inside the chat box's message
    ///     handlers, and hands the text straight back.
    /// </summary>
    /// <remarks>
    ///     Vanilla wraps here only to work out how tall a message is; whoever draws it wraps the
    ///     text again when painting, and that second pass is what the player sees. Breaks
    ///     inserted here are decided before Chat Time prepends its timestamp, so they never match
    ///     the width the line really needs -- they only force a second break in the wrong place.
    ///
    ///     Messages this mod draws are wrapped once, at draw time. Messages another mod draws are
    ///     wrapped by <see cref="WrapMessage" />, which runs late enough to see every
    ///     snippet, timestamp included.
    /// </remarks>
    public static string PrepareMessageText(string text, SpriteFont font, int width)
    {
        _ = font;
        _ = width;
        return text ?? string.Empty;
    }

    /// <summary>
    ///     Breaks a finished message into lines that fit inside the chat box.
    /// </summary>
    /// <remarks>
    ///     Applied to every message, not just the ones another mod draws. Deciding that from the
    ///     mod registry, or from spotting an item marker in the text, was wrong twice: both fail
    ///     silently, leaving the message reserving the right height and painting a single line
    ///     that runs off the right edge. Wrapping unconditionally has no such failure mode --
    ///     this mod's own layout re-measures at the same budget and so adds no further breaks.
    ///
    ///     It matters most for Item Chat Link, which wraps at a hardcoded 888px measured from the
    ///     chat box's left edge and only after drawing the segment that crossed it. Messages are
    ///     drawn 12px inside the box, so its own lines end past the right border, and a run of
    ///     plain text before the first link is one segment it cannot break at all.
    ///
    ///     Runs once the message is fully built, so anything another mod prepended -- Chat Time's
    ///     timestamp -- is measured as part of the first line rather than overflowing it.
    /// </remarks>
    public static void WrapMessage(ChatMessage message)
    {
        if (message.message == null || message.message.Count == 0)
            return;

        SpriteFont? font = ChatBox.messageFont(message.language);
        if (font == null)
            return;

        string plain = ChatMessage.makeMessagePlaintext(message.message, false);
        string wrapped = WrapLinkAware(plain, font);
        if (string.Equals(plain, wrapped, StringComparison.Ordinal))
            return;

        List<ChatSnippet> rebuilt = ParseSnippets(wrapped, message.language);
        message.message.Clear();
        message.message.AddRange(rebuilt);
    }

    /// <summary>Breaks <paramref name="text" /> into lines that fit, keeping links whole.</summary>
    private static string WrapLinkAware(string text, SpriteFont font)
    {
        StringBuilder result = new();
        float spaceWidth = font.MeasureString(" ").X;
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                result.Append(Environment.NewLine);

            float x = 0f;
            bool firstOnLine = true;

            foreach (string word in SplitIntoWords(lines[i]))
            {
                float needed = MeasureVisible(word, font);
                if (!firstOnLine)
                    needed += spaceWidth;

                // A word already alone on its line is never wrapped, or one too wide for the
                // chat box would push itself down forever.
                if (!firstOnLine && x + needed > MaxLineWidth)
                {
                    result.Append(Environment.NewLine);
                    x = 0f;
                    firstOnLine = true;
                    needed = MeasureVisible(word, font);
                }

                if (!firstOnLine)
                    result.Append(' ');

                result.Append(word);
                x += needed;
                firstOnLine = false;
            }
        }

        return result.ToString();
    }

    /// <summary>Width <paramref name="unit" /> actually paints as.</summary>
    /// <remarks>
    ///     Item Chat Link draws only the "[Name]" half of a link and hides the marker that
    ///     follows it, so counting the marker would end lines far short of the border.
    /// </remarks>
    private static float MeasureVisible(string unit, SpriteFont font)
    {
        if (unit.IndexOf(ItemLinkOpen, StringComparison.Ordinal) < 0)
            return unit.Length == 0 ? 0f : font.MeasureString(unit).X;

        // Links written back to back carry no space between them, so one "word" can hold several
        // of them. Stopping at the first marker measured only the first link and let the rest of
        // the run overflow the line.
        StringBuilder visible = new(unit.Length);
        for (int i = 0; i < unit.Length; i++)
        {
            int markerEnd = FindItemMarkerEnd(unit, i);
            if (markerEnd >= 0)
            {
                i = markerEnd;
                continue;
            }

            visible.Append(unit[i]);
        }

        return visible.Length == 0 ? 0f : font.MeasureString(visible.ToString()).X;
    }

    /// <summary>
    ///     Lines a message occupies when another mod draws it -- Item Chat Link takes over any
    ///     message holding an item link.
    /// </summary>
    /// <remarks>
    ///     Measured a word at a time at <see cref="MaxLineWidth" />, which is narrower than the
    ///     888px those drawers wrap at. Erring narrow reserves one line too many at worst and
    ///     leaves a gap; erring wide would let the message paint over its neighbours.
    /// </remarks>
    public static int CountMessageLines(ChatMessage message)
    {
        if (message.message == null || message.message.Count == 0)
            return 1;

        SpriteFont? font = ChatBox.messageFont(message.language);
        if (font == null)
            return 1;

        float spaceWidth = font.MeasureString(" ").X;
        float x = 0f;
        int lines = 1;

        foreach (ChatSnippet snippet in message.message)
        {
            if (snippet.emojiIndex != -1)
            {
                Advance(ref x, ref lines, snippet.myLength);
                continue;
            }

            if (snippet.message == null)
                continue;

            if (snippet.message.Equals(Environment.NewLine, StringComparison.Ordinal))
            {
                x = 0f;
                lines++;
                continue;
            }

            List<string> words = SplitIntoWords(snippet.message);
            for (int i = 0; i < words.Count; i++)
            {
                float width = MeasureVisible(words[i], font);
                if (i < words.Count - 1)
                    width += spaceWidth;

                Advance(ref x, ref lines, width);
            }
        }

        return lines;
    }

    /// <summary>
    ///     Builds the snippet list for <paramref name="plaintext" /> exactly the way vanilla
    ///     <see cref="ChatMessage.parseMessageForEmoji" /> does.
    /// </summary>
    /// <remarks>
    ///     The text box needs snippets to lay out the line the player is typing, not to show a
    ///     message to anyone. Getting them by calling <c>parseMessageForEmoji</c> on a scratch
    ///     <see cref="ChatMessage" /> also runs every other mod's patches on that method, and a
    ///     mod that adds a snippet there adds it to the input box too: Chat Time prepends a
    ///     timestamp, so the caret and the selection end up measured against text that is not in
    ///     the input, and both drift a little further every second. Parsing here keeps the input
    ///     box independent of whatever else is done to chat messages.
    /// </remarks>
    public static List<ChatSnippet> ParseSnippets(string? plaintext, LocalizedContentManager.LanguageCode language)
    {
        List<ChatSnippet> snippets = new();
        if (plaintext == null)
            return snippets;

        StringBuilder sb = new();
        for (int i = 0; i < plaintext.Length; i++)
        {
            if (plaintext[i] != '[')
            {
                sb.Append(plaintext[i]);
                continue;
            }

            // Vanilla flushes what it has before deciding whether the bracket opens a tag, so
            // the text either side of one lands in separate snippets. Keep that: snippet
            // boundaries decide where lines can break.
            if (sb.Length > 0)
                BreakNewLines(snippets, sb, language);
            sb.Clear();

            int tagCloseIndex = plaintext.IndexOf(']', i);
            int nextOpenIndex = i + 1 < plaintext.Length ? plaintext.IndexOf('[', i + 1) : -1;
            if (tagCloseIndex == -1 || (nextOpenIndex != -1 && nextOpenIndex < tagCloseIndex))
            {
                sb.Append('[');
                continue;
            }

            string sub = plaintext.Substring(i + 1, tagCloseIndex - i - 1);
            if (int.TryParse(sub, out int emojiIndex))
            {
                if (emojiIndex < EmojiMenu.totalEmojis)
                    snippets.Add(new ChatSnippet(emojiIndex));
            }
            else if (ChatMessage.getColorFromName(sub).Equals(Color.White))
            {
                // Not a colour name, so the brackets are literal text and stay in the line.
                sb.Append('[').Append(sub).Append(']');
            }

            i = tagCloseIndex;
        }

        if (sb.Length > 0)
            BreakNewLines(snippets, sb, language);

        return snippets;
    }

    private static void BreakNewLines(List<ChatSnippet> snippets, StringBuilder sb,
        LocalizedContentManager.LanguageCode language)
    {
        string[] split = sb.ToString().Split(Environment.NewLine);
        for (int i = 0; i < split.Length; i++)
        {
            snippets.Add(new ChatSnippet(split[i], language));
            if (i != split.Length - 1)
                snippets.Add(new ChatSnippet(Environment.NewLine, language));
        }
    }

    /// <summary>Adds one word to the current line, wrapping first if it will not fit.</summary>
    private static void Advance(ref float x, ref int lines, float width)
    {
        // A word already alone on its line is never wrapped, or one too wide for the chat box
        // would push itself down forever.
        if (x > 0f && x + width > MaxLineWidth)
        {
            x = 0f;
            lines++;
        }

        x += width;
    }

    /// <summary>
    ///     Splits <paramref name="text" /> into pieces that each render on a single line for a
    ///     player who does not have this mod.
    /// </summary>
    /// <remarks>
    ///     A vanilla client reserves a message's height from text wrapped by
    ///     <c>Game1.parseText</c> at the chat box width, then wraps it a second time at a
    ///     hardcoded 888px when it draws. Vanilla messages never wrap, so the two passes never
    ///     get the chance to disagree. Longer ones do, and the drawn text spills over its
    ///     neighbours. Nothing can be patched on that client, so the only fix is to not send it
    ///     anything that wraps.
    /// </remarks>
    /// <param name="text">The message body, without any trailing colour tag.</param>
    /// <param name="fits">
    ///     Whether a candidate piece still fits on one line once the receiving client has
    ///     formatted and wrapped it. Asking the game rather than measuring here keeps the
    ///     answer right for Japanese, Chinese and Thai, which wrap per character.
    /// </param>
    public static List<string> SplitForVanillaClients(string text, Func<string, bool> fits)
    {
        List<string> chunks = new();
        if (string.IsNullOrEmpty(text))
        {
            chunks.Add(text);
            return chunks;
        }

        StringBuilder current = new();

        foreach (string word in SplitIntoWords(text))
        {
            string candidate = current.Length == 0 ? word : $"{current} {word}";
            if (fits(candidate))
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();

                if (fits(word))
                {
                    current.Append(word);
                    continue;
                }
            }

            // The word does not fit on a line of its own, so it has to be cut.
            List<string> pieces = SplitOversizedWord(word, fits);
            for (int i = 0; i < pieces.Count - 1; i++)
                chunks.Add(pieces[i]);

            // Leave the tail open so the words after it can share its line.
            current.Append(pieces[^1]);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks;
    }

    private static List<string> SplitOversizedWord(string word, Func<string, bool> fits)
    {
        List<string> pieces = new();
        StringBuilder current = new();

        foreach (string unit in Tokenize(word))
        {
            if (current.Length > 0 && !fits(current.ToString() + unit))
            {
                pieces.Add(current.ToString());
                current.Clear();
            }

            current.Append(unit);
        }

        // A single unit too wide for a line still has to go somewhere; one overflowing line
        // beats dropping the player's text.
        pieces.Add(current.ToString());
        return pieces;
    }

    /// <summary>
    ///     Splits <paramref name="text" /> on spaces, keeping tags and item links whole.
    /// </summary>
    /// <remarks>
    ///     Item Chat Link labels a link with the item's display name, and plenty of those have a
    ///     space in them. Splitting on spaces first would break "[Prismatic Shard]{icl:...}" in
    ///     two and leave the halves on different lines, which is exactly the pairing that mod
    ///     needs intact.
    /// </remarks>
    private static List<string> SplitIntoWords(string text)
    {
        List<string> words = new();
        StringBuilder current = new();

        foreach (string unit in Tokenize(text))
        {
            if (unit == " ")
            {
                words.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(unit);
        }

        words.Add(current.ToString());
        return words;
    }

    /// <summary>
    ///     Walks <paramref name="text" /> as indivisible units: one character, or one whole tag.
    /// </summary>
    /// <remarks>
    ///     Emoji tags, colour tags and Item Chat Link's item links are atomic. Cutting one in
    ///     half turns it into literal text on the receiving client, or worse, swallows the rest
    ///     of the message into a tag that was never closed.
    /// </remarks>
    private static IEnumerable<string> Tokenize(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                int markerEnd = FindItemMarkerEnd(text, i);
                if (markerEnd != -1)
                {
                    yield return text.Substring(i, markerEnd - i + 1);
                    i = markerEnd;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i);
                int nextOpen = i + 1 < text.Length ? text.IndexOf('[', i + 1) : -1;
                if (close != -1 && (nextOpen == -1 || nextOpen > close))
                {
                    // An item link is the display name tag and the marker straight after it.
                    int end = close;
                    int markerEnd = FindItemMarkerEnd(text, close + 1);
                    if (markerEnd != -1)
                        end = markerEnd;

                    yield return text.Substring(i, end - i + 1);
                    i = end;
                    continue;
                }
            }

            yield return text[i].ToString();
        }
    }

    /// <summary>
    ///     Index of the closing brace of the item marker starting at <paramref name="index" />,
    ///     or -1 if there is no marker there.
    /// </summary>
    private static int FindItemMarkerEnd(string text, int index)
    {
        if (index >= text.Length || string.CompareOrdinal(text, index, ItemLinkOpen, 0, ItemLinkOpen.Length) != 0)
            return -1;

        return text.IndexOf('}', index + ItemLinkOpen.Length);
    }
}
