using System;
using System.Collections.Generic;
using System.Text;

namespace JustMd.Core.Editing
{
    /// <summary>Gives LF-only text the line endings of the text it was edited from.</summary>
    public static class LineEndings
    {
        /// <summary>
        /// Splits both texts into lines; lines in the common prefix and common suffix (by content,
        /// non-overlapping) take the ending they have in <paramref name="original"/> ("\r\n", "\n"
        /// or "\r"); every other separator becomes <paramref name="fallback"/>.
        /// </summary>
        public static string Apply(string lfText, string original, string fallback)
        {
            List<Line> lines = Split(lfText);
            List<Line> anchors = Split(original);
            int shift = anchors.Count - lines.Count;
            int limit = Math.Min(lines.Count, anchors.Count);

            int prefix = 0;
            while (prefix < limit && SameContent(lfText, lines[prefix], original, anchors[prefix]))
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < limit - prefix
                   && SameContent(lfText, lines[lines.Count - 1 - suffix], original, anchors[anchors.Count - 1 - suffix]))
            {
                suffix++;
            }

            var result = new StringBuilder(lfText.Length + lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                Line line = lines[i];
                result.Append(lfText, line.Start, line.Length);
                if (i == lines.Count - 1)
                {
                    break; // what follows the last separator has no ending
                }

                // The original's last line has no ending to give, even when its content matches.
                if (i < prefix && i < anchors.Count - 1)
                {
                    AppendEnding(result, original, anchors[i]);
                }
                else if (i >= lines.Count - suffix)
                {
                    AppendEnding(result, original, anchors[i + shift]);
                }
                else
                {
                    result.Append(fallback);
                }
            }

            return result.ToString();
        }

        private static void AppendEnding(StringBuilder result, string text, Line line) =>
            result.Append(text, line.Start + line.Length, line.EndingLength);

        private static bool SameContent(string a, Line lineA, string b, Line lineB) =>
            lineA.Length == lineB.Length
            && string.CompareOrdinal(a, lineA.Start, b, lineB.Start, lineA.Length) == 0;

        /// <summary>Always at least one line; the last one is what follows the last separator.</summary>
        private static List<Line> Split(string text)
        {
            var lines = new List<Line>();
            int start = 0;
            while (true)
            {
                int offset = text.AsSpan(start).IndexOfAny('\r', '\n');
                if (offset < 0)
                {
                    lines.Add(new Line(start, text.Length - start, 0));
                    return lines;
                }

                int end = start + offset;
                int endingLength = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
                lines.Add(new Line(start, offset, endingLength));
                start = end + endingLength;
            }
        }

        private readonly record struct Line(int Start, int Length, int EndingLength);
    }
}
