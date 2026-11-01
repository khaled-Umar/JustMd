using System;
using JustMd.Core;

namespace JustMd.Tests
{
    public class LinkClassifierTests
    {
        [Theory]
        [InlineData("https://example.com/path?q=1#frag", "https")]
        [InlineData("http://example.com", "http")]
        [InlineData("HTTPS://EXAMPLE.COM", "https")]
        [InlineData("mailto:someone@example.com", "mailto")]
        [InlineData("  https://example.com  ", "https")]
        public void Web_and_mail_links_are_external(string href, string scheme)
        {
            LinkTarget target = LinkClassifier.Classify(href, @"C:\docs\readme.md");

            Assert.Equal(LinkKind.External, target.Kind);
            Assert.Equal(scheme, target.Scheme);
            Assert.NotNull(target.Uri);
        }

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("JaVaScRiPt:alert(1)")]
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("data:text/html,<script>alert(1)</script>")]
        [InlineData("vbscript:msgbox")]
        [InlineData("ms-settings:privacy")]
        [InlineData("search-ms:query=x")]
        [InlineData("ftp://example.com/file")]
        [InlineData("tel:+123")]
        [InlineData("https://")]
        public void Other_schemes_are_blocked(string href)
        {
            LinkTarget target = LinkClassifier.Classify(href, @"C:\docs\readme.md");

            Assert.Equal(LinkKind.Blocked, target.Kind);
            Assert.Null(target.Uri);
        }

        [Theory]
        [InlineData("//evil.example/share")]
        [InlineData(@"\\evil\share\x.md")]
        public void Network_and_protocol_relative_links_are_blocked(string href)
        {
            Assert.Equal(LinkKind.Blocked, LinkClassifier.Classify(href, @"C:\docs\readme.md").Kind);
        }

        [Theory]
        [InlineData("#section", "section")]
        [InlineData("#caf%C3%A9", "caf\u00e9")]
        [InlineData("#", "")]
        public void Anchors_are_in_page(string href, string fragment)
        {
            LinkTarget target = LinkClassifier.Classify(href, null);

            Assert.Equal(LinkKind.Anchor, target.Kind);
            Assert.Equal(fragment, target.Fragment);
        }

        [Fact]
        public void Empty_link_is_none()
        {
            Assert.Equal(LinkKind.None, LinkClassifier.Classify("  ", @"C:\a.md").Kind);
            Assert.Equal(LinkKind.None, LinkClassifier.Classify(null, @"C:\a.md").Kind);
        }

        [Fact]
        public void Relative_link_to_existing_markdown_opens_in_viewer()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile(@"docs\readme.md", "# a");
            string other = temp.WriteFile(@"guide\setup guide.markdown", "# b");

            LinkTarget target = LinkClassifier.Classify("../guide/setup%20guide.markdown#install", doc);

            Assert.Equal(LinkKind.LocalMarkdown, target.Kind);
            Assert.Equal(other, target.LocalPath, ignoreCase: true);
            Assert.Equal("install", target.Fragment);
        }

        [Fact]
        public void Relative_link_with_backslashes_and_query_resolves()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "# a");
            string other = temp.WriteFile(@"sub\notes.mdown", "# b");

            LinkTarget target = LinkClassifier.Classify(@"sub\notes.mdown?raw=1", doc);

            Assert.Equal(LinkKind.LocalMarkdown, target.Kind);
            Assert.Equal(other, target.LocalPath, ignoreCase: true);
        }

        [Fact]
        public void Relative_link_to_missing_markdown_is_local_file()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "# a");

            LinkTarget target = LinkClassifier.Classify("missing.md", doc);

            Assert.Equal(LinkKind.LocalFile, target.Kind);
            Assert.Equal(temp.Combine("missing.md"), target.LocalPath, ignoreCase: true);
        }

        [Theory]
        [InlineData("tool.exe")]
        [InlineData("script.bat")]
        [InlineData("data.json")]
        [InlineData("sub")]
        public void Relative_link_to_non_markdown_is_local_file_never_opened(string href)
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "# a");
            temp.WriteFile("tool.exe", "MZ");
            temp.WriteFile("script.bat", "echo");
            temp.WriteFile("data.json", "{}");
            temp.WriteFile(@"sub\x.md", "x");

            Assert.Equal(LinkKind.LocalFile, LinkClassifier.Classify(href, doc).Kind);
        }

        [Fact]
        public void Relative_link_without_document_is_blocked()
        {
            Assert.Equal(LinkKind.Blocked, LinkClassifier.Classify("other.md", null).Kind);
        }

        [Fact]
        public void Query_or_fragment_only_link_targets_current_document()
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "# a");

            LinkTarget target = LinkClassifier.Classify("?x=1#top", doc);

            Assert.Equal(LinkKind.LocalMarkdown, target.Kind);
            Assert.Equal(doc, target.LocalPath, ignoreCase: true);
            Assert.Equal("top", target.Fragment);
        }

        [Theory]
        [InlineData("readme.md:secret")]
        [InlineData("readme.md::$DATA")]
        public void Alternate_data_streams_are_blocked(string href)
        {
            using var temp = new TempDirectory();
            string doc = temp.WriteFile("readme.md", "# a");

            Assert.Equal(LinkKind.Blocked, LinkClassifier.Classify(href, doc).Kind);
        }

        [Fact]
        public void TryValidateExternal_is_strict()
        {
            Assert.True(LinkClassifier.TryValidateExternal("https://example.com/a b", out Uri? ok));
            Assert.Equal("https://example.com/a%20b", ok!.AbsoluteUri);

            Assert.False(LinkClassifier.TryValidateExternal("javascript:alert(1)", out _));
            Assert.False(LinkClassifier.TryValidateExternal("file:///C:/x", out _));
            Assert.False(LinkClassifier.TryValidateExternal("relative/path", out _));
            Assert.False(LinkClassifier.TryValidateExternal("", out _));
            Assert.False(LinkClassifier.TryValidateExternal(null, out _));
            Assert.False(LinkClassifier.TryValidateExternal("myapp://do-something", out _));
        }

        [Fact]
        public void Display_parts_emphasise_real_host_even_with_user_info()
        {
            Assert.True(LinkClassifier.TryValidateExternal("https://bank.example@evil.example/login?x=1#y", out Uri? uri));

            ExternalLinkParts parts = LinkClassifier.GetDisplayParts(uri!);

            Assert.Equal("evil.example", parts.Host);
            Assert.Equal("https://bank.example@", parts.Prefix);
            Assert.Equal("/login?x=1#y", parts.Suffix);
        }

        [Fact]
        public void Display_parts_include_non_default_port()
        {
            Assert.True(LinkClassifier.TryValidateExternal("http://example.com:8080/a", out Uri? uri));

            ExternalLinkParts parts = LinkClassifier.GetDisplayParts(uri!);

            Assert.Equal("http://", parts.Prefix);
            Assert.Equal("example.com", parts.Host);
            Assert.Equal(":8080/a", parts.Suffix);
        }

        [Fact]
        public void Display_parts_for_mailto_show_domain()
        {
            Assert.True(LinkClassifier.TryValidateExternal("mailto:someone@example.com?subject=Hi", out Uri? uri));

            ExternalLinkParts parts = LinkClassifier.GetDisplayParts(uri!);

            Assert.Equal("example.com", parts.Host);
            Assert.Equal("mailto:someone@", parts.Prefix);
            Assert.Equal("?subject=Hi", parts.Suffix);
        }
    }
}
