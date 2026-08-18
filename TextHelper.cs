using System.Text;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ChatImprovements;

public static class TextHelper
{
    private const int WidthPadding = 14;
    private const float MaxLineWidth = 872f;

    public static string ParseTextWithWrapping(string text, SpriteFont font, int width)
    {
        // Reduce visible width to prevent overlap with the right border
        width = Math.Max(0, width - WidthPadding);

        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder result = new();
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.AppendLine();

            string currentLine = lines[i];
            if (string.IsNullOrEmpty(currentLine)) continue;

            ProcessLine(result, currentLine, font, width);
        }

        return result.ToString();
    }

    private static void ProcessLine(StringBuilder result, string line, SpriteFont font, int width)
    {
        string[] words = line.Split(' ');
        float currentLineWidth = 0f;
        float spaceWidth = font.MeasureString(" ").X;
        bool firstWordInLine = true;

        foreach (string word in words)
        {
            float wordWidth = IsColorTag(word) ? 0f : font.MeasureString(word).X;

            if (wordWidth > width)
            {
                HandleLongWord(result, word, font, width, ref currentLineWidth, ref firstWordInLine);
                continue;
            }

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
                    result.Append(' ');
                    currentLineWidth += spaceWidth;
                }
            }

            result.Append(word);
            currentLineWidth += wordWidth;
            firstWordInLine = false;
        }
    }

    private static void HandleLongWord(StringBuilder result, string word, SpriteFont font, int width, ref float currentLineWidth, ref bool firstWordInLine)
    {
        // If we have content on the current line, wrap first
        if (!firstWordInLine)
        {
            result.AppendLine();
            currentLineWidth = 0f;
            firstWordInLine = true;
        }

        string remainingWord = word;
        while (font.MeasureString(remainingWord).X > width)
        {
            int splitIndex = FindSplitIndex(remainingWord, font, width);
            
            result.Append(remainingWord.Substring(0, splitIndex));
            result.AppendLine();
            
            remainingWord = remainingWord.Substring(splitIndex);
            firstWordInLine = true;
            currentLineWidth = 0f;
        }

        result.Append(remainingWord);
        currentLineWidth = font.MeasureString(remainingWord).X;
        firstWordInLine = false;
    }

    private static int FindSplitIndex(string text, SpriteFont font, int width)
    {
        int splitIndex = 0;
        float partialWidth = 0f;
        
        for (int k = 0; k < text.Length; k++)
        {
            float charWidth = font.MeasureString(text[k].ToString()).X;
            if (partialWidth + charWidth > width)
                break;

            partialWidth += charWidth;
            splitIndex++;
        }

        return Math.Max(1, splitIndex);
    }

    private static bool IsColorTag(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
        if (s[0] != '[' || s[s.Length - 1] != ']') return false;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (!char.IsLetter(s[i])) return false;
        }
        return true;
    }

    public static int CountMessageLines(ChatMessage message)
    {
        if (message.message == null || message.message.Count == 0)
            return 1;

        // Replicate the EXACT logic from ChatMessage.draw()
        float xPositionSoFar = 0f;
        int lineCount = 1; // Start with 1 line
        
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
            if (xPositionSoFar >= MaxLineWidth)
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
}
