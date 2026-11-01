using System.Text;
using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd.Tests
{
    public class MarkdownRendererTests
    {
        private readonly MarkdownRenderer _renderer = new();

        [Fact]
        public void Renders_pipe_tables()
        {
            string html = _renderer.RenderHtml("| A | B |\n|---|--:|\n| 1 | 2 |\n", null);

            Assert.Contains("<table>", html);
            Assert.Contains("<th>A</th>", html);
            Assert.Contains("<td>1</td>", html);
            Assert.Contains("text-align: right", html);
        }

        [Fact]
        public void Renders_task_lists_as_disabled_checkboxes()
        {
            string html = _renderer.RenderHtml("- [x] done\n- [ ] todo\n", null);

            Assert.Contains("contains-task-list", html);
            Assert.Contains("type=\"checkbox\"", html);
            Assert.Contains("checked=\"checked\"", html);
            Assert.Contains("disabled=\"disabled\"", html);
        }

        [Fact]
        public void Fenced_code_gets_language_class_and_escaped_content()
        {
            string html = _renderer.RenderHtml("```csharp\nvar x = a < b;\n```\n", null);

            Assert.Contains("<pre><code class=\"language-csharp\">", html);
            Assert.Contains("a &lt; b", html);
        }

        [Fact]
        public void Yaml_front_matter_is_hidden()
        {
            string html = _renderer.RenderHtml("---\ntitle: Secret Title\ntags: [a, b]\n---\n# Visible\n", null);

            Assert.DoesNotContain("Secret Title", html);
            Assert.DoesNotContain("tags:", html);
            Assert.Contains("Visible</h1>", html);
        }

        [Fact]
        public void Headings_get_github_style_ids()
        {
            string html = _renderer.RenderHtml("## Getting Started Now\n", null);

            Assert.Contains("<h2 id=\"user-content-getting-started-now\">", html);
        }

        [Theory]
        [InlineData("Title", "user-content-title")]
        [InlineData("Links", "user-content-links")]
        [InlineData("Images", "user-content-images")]
        [InlineData("Location", "user-content-location")]
        [InlineData("Open", "user-content-open")]
        [InlineData("Content", "user-content-content")]
        public void Heading_ids_are_prefixed_so_they_cannot_clobber_dom_names(string heading, string expectedId)
        {
            // Unprefixed, ids such as "title" or "links" are stripped by DOMPurify (DOM clobbering
            // protection) and "content" would collide with the viewer's own element ids.
            string html = _renderer.RenderHtml("# " + heading + "\n", null);

            Assert.Contains("id=\"" + expectedId + "\"", html);
        }

        [Fact]
        public void Duplicate_headings_get_unique_prefixed_ids()
        {
            string html = _renderer.RenderHtml("## Setup\n\n## Setup\n", null);

            Assert.Contains("id=\"user-content-setup\"", html);
            Assert.Contains("id=\"user-content-setup-1\"", html);
        }

        [Fact]
        public void Root_relative_and_drive_absolute_images_are_rewritten()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile(@"docs\readme.md", "x");
            string root = System.IO.Path.GetPathRoot(doc)!;
            string absolute = temp.Combine("pics", "a.png").Replace('\\', '/');

            string html = _renderer.RenderHtml("![r](/assets/r.png) ![a](" + absolute + ")", doc);

            Assert.Contains(LocalImageUrls.FromPath(System.IO.Path.Combine(root, "assets", "r.png")), html);
            Assert.Contains(LocalImageUrls.FromPath(temp.Combine("pics", "a.png")), html);
        }

        [Fact]
        public void Supports_strikethrough_footnotes_autolinks_and_emoji()
        {
            string html = _renderer.RenderHtml("~~old~~ text[^1] see https://example.com :smile:\n\n[^1]: The note.\n", null);

            Assert.Contains("<del>old</del>", html);
            Assert.Contains("class=\"footnote-ref\"", html);
            Assert.Contains("The note.", html);
            Assert.Contains("<a href=\"https://example.com\">", html);
            Assert.Contains("\U0001F604", html);
        }

        [Fact]
        public void Smileys_are_not_converted()
        {
            string html = _renderer.RenderHtml("Call f(:) here :)\n", null);

            Assert.Contains(":)", html);
        }

        [Fact]
        public void Raw_html_is_passed_through_for_the_page_sanitiser()
        {
            // Sanitising happens in the viewer page with DOMPurify; the renderer must not
            // silently drop allowed raw HTML such as <details> or <kbd>.
            string html = _renderer.RenderHtml("<details><summary>More</summary>Body</details>\n\nPress <kbd>Ctrl</kbd>\n", null);

            Assert.Contains("<details>", html);
            Assert.Contains("<kbd>Ctrl</kbd>", html);
        }

        [Fact]
        public void Github_alerts_are_rendered()
        {
            string html = _renderer.RenderHtml("> [!WARNING]\n> Careful\n", null);

            Assert.Contains("markdown-alert-warning", html);
        }

        [Fact]
        public void Math_and_media_embeds_are_not_enabled()
        {
            string html = _renderer.RenderHtml("$x^2$\n\n![v](https://www.youtube.com/watch?v=abc)\n", null);

            Assert.DoesNotContain("class=\"math\"", html);
            Assert.DoesNotContain("<iframe", html);
        }

        [Fact]
        public void Relative_images_are_rewritten_to_the_image_host()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile(@"docs\readme.md", "x");

            string html = _renderer.RenderHtml("![logo](img/logo.png) ![up](../shared/a%20b.jpg)", doc);

            string expectedLogo = LocalImageUrls.FromPath(temp.Combine("docs", "img", "logo.png"));
            string expectedUp = LocalImageUrls.FromPath(temp.Combine("shared", "a b.jpg"));
            Assert.Contains("src=\"" + expectedLogo + "\"", html);
            Assert.Contains("src=\"" + expectedUp + "\"", html);
            Assert.StartsWith("https://img.justmd.example/f/", expectedLogo);
        }

        [Fact]
        public void Reference_style_images_are_rewritten()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "x");

            string html = _renderer.RenderHtml("![pic][p]\n\n[p]: pics/p.gif\n", doc);

            Assert.Contains(LocalImageUrls.FromPath(temp.Combine("pics", "p.gif")), html);
        }

        [Theory]
        [InlineData("https://example.com/a.png")]
        [InlineData("data:image/png;base64,AAAA")]
        [InlineData("//cdn.example.com/a.png")]
        public void Absolute_and_special_images_are_left_alone(string url)
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "x");

            string html = _renderer.RenderHtml("![x](" + url + ")", doc);

            Assert.DoesNotContain("img.justmd.example", html);
        }

        [Fact]
        public void Without_a_document_path_images_are_not_rewritten()
        {
            string html = _renderer.RenderHtml("![x](a.png)", null);

            Assert.Contains("src=\"a.png\"", html);
        }

        [Fact]
        public void Rtl_wrapper_renders_a_div_around_parsed_markdown()
        {
            string text = DocumentText.Parse("# Title\n\nBody\n", new UTF8Encoding(false)).With("# Title\n\nBody", true).Compose();

            string html = _renderer.RenderHtml(text, null);

            Assert.Matches("^<div dir=\"rtl\">\\s*<h1 [^>]*>Title</h1>\\s*<p>Body</p>\\s*</div>\\s*$", html);
        }

        [Fact]
        public void Rtl_wrapper_after_front_matter_renders_the_same()
        {
            string text = DocumentText.Parse("---\nt: Secret\n---\n\n# Title\n", new UTF8Encoding(false)).With("# Title", true).Compose();

            string html = _renderer.RenderHtml(text, null);

            Assert.DoesNotContain("Secret", html);
            Assert.Matches("^<div dir=\"rtl\">\\s*<h1 ", html);
        }

        [Fact]
        public void Links_are_not_rewritten()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "x");

            string html = _renderer.RenderHtml("[other](other.md#part)", doc);

            Assert.Contains("href=\"other.md#part\"", html);
        }
    }
}
