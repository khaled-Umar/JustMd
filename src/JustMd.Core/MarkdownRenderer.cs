using System;
using System.IO;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JustMd.Core
{
    /// <summary>
    /// Converts Markdown to HTML with GitHub-flavoured extensions.
    /// <para>
    /// The output is NOT safe on its own: raw HTML from the document is passed through and is
    /// sanitised by DOMPurify inside the viewer page before it is inserted.
    /// </para>
    /// </summary>
    public sealed class MarkdownRenderer
    {
        /// <summary>Prefix added to every generated heading id (same convention as GitHub).</summary>
        public const string HeadingIdPrefix = "user-content-";

        private readonly MarkdownPipeline _pipeline = BuildPipeline();

        /// <summary>
        /// Advanced extensions minus the heavy or risky ones (math, diagrams, media embeds that emit
        /// iframes, generic attributes that would let a document set arbitrary attributes).
        /// </summary>
        public static MarkdownPipeline BuildPipeline() =>
            new MarkdownPipelineBuilder()
                .UseYamlFrontMatter()
                .UseAlertBlocks()
                .UseAbbreviations()
                .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
                .UseCitations()
                .UseCustomContainers()
                .UseDefinitionLists()
                .UseEmphasisExtras()
                .UseFigures()
                .UseFooters()
                .UseFootnotes()
                .UseGridTables()
                .UsePipeTables()
                .UseListExtras()
                .UseTaskLists()
                .UseAutoLinks()
                .UseEmojiAndSmiley(enableSmileys: false)
                .Build();

        /// <summary>
        /// Renders <paramref name="markdown"/> to HTML. Relative image references are rewritten to
        /// the local-image virtual host, resolved against the directory of <paramref name="documentPath"/>.
        /// </summary>
        public string RenderHtml(string markdown, string? documentPath)
        {
            MarkdownDocument document = Markdown.Parse(markdown ?? string.Empty, _pipeline);

            // Like GitHub, prefix generated heading ids so a heading such as "Title" or "Open"
            // can never collide with a DOM property (DOMPurify would strip it) or with the
            // viewer page's own element ids. The page maps "#fragment" to the prefixed id.
            foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
            {
                HtmlAttributes? attributes = heading.TryGetAttributes();
                if (!string.IsNullOrEmpty(attributes?.Id) && !attributes!.Id!.StartsWith(HeadingIdPrefix, StringComparison.Ordinal))
                {
                    attributes.Id = HeadingIdPrefix + attributes.Id;
                }
            }

            string? baseDirectory = GetBaseDirectory(documentPath);
            if (baseDirectory != null)
            {
                foreach (LinkInline link in document.Descendants<LinkInline>())
                {
                    if (!link.IsImage)
                    {
                        continue;
                    }

                    string? rewritten = LocalImageUrls.RewriteRelative(link.Url, baseDirectory);
                    if (rewritten != null)
                    {
                        link.Url = rewritten;
                    }
                }
            }

            using (var writer = new StringWriter())
            {
                var renderer = new HtmlRenderer(writer);
                _pipeline.Setup(renderer);
                renderer.Render(document);
                writer.Flush();
                return writer.ToString();
            }
        }

        private static string? GetBaseDirectory(string? documentPath)
        {
            if (string.IsNullOrEmpty(documentPath))
            {
                return null;
            }

            string? full = LocalPaths.TryGetFullPath(documentPath!);
            return full == null ? null : Path.GetDirectoryName(full);
        }
    }
}
