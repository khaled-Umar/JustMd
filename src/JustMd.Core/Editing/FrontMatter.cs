using System;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace JustMd.Core.Editing
{
    /// <summary>Separates YAML front matter from the Markdown after it.</summary>
    public static class FrontMatter
    {
        private static readonly MarkdownPipeline Pipeline = MarkdownRenderer.BuildPipeline();

        /// <summary>
        /// Uses the same parser as rendering, so the editor hides exactly what view mode hides.
        /// The front matter runs through the closing line and always ends with a newline; it is
        /// empty when the text has none.
        /// </summary>
        public static (string FrontMatter, string Body) Split(string text)
        {
            if (!text.StartsWith("---", StringComparison.Ordinal)
                || Markdown.Parse(text, Pipeline) is not { Count: > 0 } document
                || document[0] is not YamlFrontMatterBlock block)
            {
                return (string.Empty, text);
            }

            int newLine = text.IndexOf('\n', Math.Min(block.Span.End, text.Length));
            return newLine < 0
                ? (text + "\n", string.Empty)
                : (text.Substring(0, newLine + 1), text.Substring(newLine + 1));
        }
    }
}
