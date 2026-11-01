using System.Linq;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JustMd.Core.Editing
{
    /// <summary>
    /// The on-disk form of a right-to-left document: a <c>&lt;div dir="rtl"&gt;</c> around the
    /// whole body, with blank lines so Markdown inside it is still parsed.
    /// </summary>
    public static class RtlWrapper
    {
        public const string OpenTag = "<div dir=\"rtl\">";

        private const string CloseTag = "</div>";

        // Up to three spaces: four or more would make the line an indented code block.
        private static readonly Regex OpenLine = new(
            "^ {0,3}<div\\s+dir\\s*=\\s*(?:\"rtl\"|'rtl'|rtl)\\s*>[ \\t]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CloseLine = new(
            "^ {0,3}</div\\s*>[ \\t]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex DivTag = new(
            "<(?<close>/)?div(?=[\\s>/])[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly MarkdownPipeline Pipeline = MarkdownRenderer.BuildPipeline();

        /// <summary>
        /// True when the wrapper's opening tag is the first non-blank line of <paramref name="body"/>
        /// and the <c>&lt;/div&gt;</c> matching it is the last. A tag with any other attribute is not
        /// a wrapper: removing it would lose that attribute.
        /// </summary>
        public static bool TryUnwrap(string body, out string inner)
        {
            inner = string.Empty;
            string text = DocumentText.TrimBlankLines(DocumentText.ToLf(body), out _);
            int firstEnd = text.IndexOf('\n');
            int lastStart = text.LastIndexOf('\n') + 1;
            if (firstEnd < 0
                || !OpenLine.IsMatch(text.Substring(0, firstEnd))
                || !CloseLine.IsMatch(text.Substring(lastStart)))
            {
                return false;
            }

            string candidate = DocumentText.TrimBlankLines(text.Substring(firstEnd + 1, lastStart - firstEnd - 1), out _);
            if (!DivsAreBalanced(candidate))
            {
                return false;
            }

            inner = candidate;
            return true;
        }

        public static string Wrap(string inner) => OpenTag + "\n\n" + inner + "\n\n" + CloseTag;

        /// <summary>
        /// Guards against <c>&lt;div dir="rtl"&gt;a&lt;/div&gt; b &lt;div&gt;c&lt;/div&gt;</c>, where the
        /// last line closes a different div. Only real HTML counts, not tags shown in code.
        /// </summary>
        private static bool DivsAreBalanced(string markdown)
        {
            int depth = 0;
            foreach (MarkdownObject node in Markdown.Parse(markdown, Pipeline).Descendants())
            {
                string? html = node switch
                {
                    HtmlBlock { Type: not HtmlBlockType.Comment } block => block.Lines.ToString(),
                    HtmlInline tag => tag.Tag,
                    _ => null,
                };

                if (html == null)
                {
                    continue;
                }

                foreach (Match match in DivTag.Matches(html).Cast<Match>())
                {
                    depth += match.Groups["close"].Success ? -1 : 1;
                    if (depth < 0)
                    {
                        return false;
                    }
                }
            }

            return depth == 0;
        }
    }
}
