using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd.Tests
{
    public class RelativeReferencesTests
    {
        [Theory]
        [InlineData(@"C:\docs\a.md", @"C:\docs\pic.png", "pic.png")]
        [InlineData(@"C:\docs\a.md", @"C:\docs\img\a b.png", "img/a%20b.png")]
        [InlineData(@"C:\docs\a.md", @"C:\docs\img\a (1).png", "img/a%20%281%29.png")]
        [InlineData(@"C:\docs\sub\a.md", @"C:\pics\p.png", "../../pics/p.png")]
        [InlineData(@"C:\DOCS\a.md", @"c:\docs\p.png", "p.png")]
        [InlineData(@"C:\docs\a.md", @"C:\docs\صورة.png", "صورة.png")]
        [InlineData(@"\\server\share\docs\a.md", @"\\server\share\img\p.png", "../img/p.png")]
        public void Same_root_gives_relative_reference(string document, string image, string expected)
        {
            Assert.Equal(expected, RelativeReferences.ForImage(document, image));
        }

        [Theory]
        [InlineData(@"C:\docs\a.md", @"D:\pics\a b.png", "D:/pics/a%20b.png")]
        [InlineData(null, @"C:\pics\p.png", "C:/pics/p.png")]
        [InlineData("", @"C:\pics\p.png", "C:/pics/p.png")]
        [InlineData(@"\\server\one\a.md", @"\\server\two\p.png", "//server/two/p.png")]
        public void Other_root_or_no_document_gives_absolute_reference(string? document, string image, string expected)
        {
            Assert.Equal(expected, RelativeReferences.ForImage(document, image));
        }

        [Theory]
        [InlineData(@"C:\docs\img\a b.png")]
        [InlineData(@"C:\docs\img\100%25 (final).png")]
        [InlineData(@"C:\docs\img\issue #4.png")]
        [InlineData(@"C:\docs\img\a&amp;b.png")]
        [InlineData(@"C:\docs\img\صورة.png")]
        [InlineData(@"C:\pics\p.png")]
        [InlineData(@"D:\pics\a b.png")]
        public void Reference_resolves_back_to_the_same_file(string image)
        {
            string reference = RelativeReferences.ForImage(@"C:\docs\a.md", image);

            Assert.Equal(LocalImageUrls.FromPath(image), LocalImageUrls.RewriteRelative(reference, @"C:\docs"));
        }

        [Fact]
        public void Reference_survives_markdown_parsing()
        {
            string reference = RelativeReferences.ForImage(@"C:\docs\a.md", @"C:\docs\img\a (1) #2.png");

            string html = new MarkdownRenderer().RenderHtml("![x](" + reference + ")", @"C:\docs\a.md");

            Assert.Contains("src=\"" + LocalImageUrls.FromPath(@"C:\docs\img\a (1) #2.png") + "\"", html);
        }
    }
}
