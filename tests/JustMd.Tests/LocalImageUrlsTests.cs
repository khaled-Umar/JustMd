using JustMd.Core;

namespace JustMd.Tests
{
    public class LocalImageUrlsTests
    {
        [Fact]
        public void Drive_paths_round_trip()
        {
            string path = @"C:\My Docs\images\diagram #1.png";

            string url = LocalImageUrls.FromPath(path);

            Assert.Equal("https://img.justmd.example/f/C%3A/My%20Docs/images/diagram%20%231.png", url);
            Assert.True(LocalImageUrls.TryGetPath(url, out string back));
            Assert.Equal(path, back);
        }

        [Fact]
        public void Unc_paths_round_trip()
        {
            string path = @"\\server\share\docs\a.png";

            string url = LocalImageUrls.FromPath(path);

            Assert.Equal("https://img.justmd.example/f/UNC/server/share/docs/a.png", url);
            Assert.True(LocalImageUrls.TryGetPath(url, out string back));
            Assert.Equal(path, back);
        }

        [Fact]
        public void Directory_base_url_has_trailing_slash()
        {
            Assert.Equal("https://img.justmd.example/f/C%3A/docs/", LocalImageUrls.DirectoryBaseUrl(@"C:\docs\"));
            Assert.Equal("https://img.justmd.example/f/C%3A/", LocalImageUrls.DirectoryBaseUrl(@"C:\"));
        }

        [Theory]
        [InlineData("https://app.justmd.example/f/C%3A/a.png")]
        [InlineData("http://img.justmd.example/f/C%3A/a.png")]
        [InlineData("https://img.justmd.example/C%3A/a.png")]
        [InlineData("https://img.justmd.example/f/notadrive/a.png")]
        [InlineData("https://img.justmd.example/f/C%3A/a%5C..%5C..%5Cb.png")]
        [InlineData("https://img.justmd.example/f/C%3A/a.png%3Astream")]
        [InlineData("https://img.justmd.example/f/UNC/server")]
        [InlineData("not a url")]
        public void Malformed_or_foreign_urls_are_rejected(string url)
        {
            Assert.False(LocalImageUrls.TryGetPath(url, out _));
        }

        [Theory]
        [InlineData(@"C:\docs\a.png", true)]
        [InlineData(@"C:\docs\a.JPG", true)]
        [InlineData(@"C:\docs\a.svg", true)]
        [InlineData(@"C:\docs\a.webp", true)]
        [InlineData(@"C:\docs\secret.txt", false)]
        [InlineData(@"C:\docs\readme.md", false)]
        [InlineData(@"C:\docs\app.exe", false)]
        [InlineData(@"C:\docs\page.html", false)]
        [InlineData(@"C:\docs\noext", false)]
        public void Only_image_extensions_are_servable(string path, bool expected)
        {
            Assert.Equal(expected, LocalImageUrls.IsServable(path, @"C:\docs\readme.md"));
        }

        [Fact]
        public void Images_on_another_drive_or_share_are_not_servable()
        {
            Assert.False(LocalImageUrls.IsServable(@"D:\other\a.png", @"C:\docs\readme.md"));
            Assert.False(LocalImageUrls.IsServable(@"\\evil\share\a.png", @"C:\docs\readme.md"));
            Assert.False(LocalImageUrls.IsServable(@"\\evil\share\a.png", @"\\server\share\readme.md"));
            Assert.True(LocalImageUrls.IsServable(@"\\server\share\img\a.png", @"\\server\share\docs\readme.md"));
        }

        [Fact]
        public void Absolute_reference_on_another_drive_is_rewritten_but_not_servable()
        {
            string? url = LocalImageUrls.RewriteRelative("D:/other/a.png", @"C:\docs");

            Assert.True(LocalImageUrls.TryGetPath(url, out string path));
            Assert.False(LocalImageUrls.IsServable(path, @"C:\docs\readme.md"));
        }

        [Fact]
        public void Root_relative_reference_on_unc_share_stays_on_that_share()
        {
            string? url = LocalImageUrls.RewriteRelative("/img/a.png", @"\\server\share\docs");

            Assert.Equal("https://img.justmd.example/f/UNC/server/share/img/a.png", url);
            Assert.True(LocalImageUrls.TryGetPath(url, out string path));
            Assert.True(LocalImageUrls.IsServable(path, @"\\server\share\docs\readme.md"));
        }

        [Fact]
        public void Nothing_is_servable_without_a_document()
        {
            Assert.False(LocalImageUrls.IsServable(@"C:\docs\a.png", null));
        }

        [Theory]
        [InlineData("img/a.png", @"C:\docs\img\a.png")]
        [InlineData("./img/a.png?v=2#x", @"C:\docs\img\a.png")]
        [InlineData("../a.png", @"C:\a.png")]
        [InlineData("../../../../a.png", @"C:\a.png")]
        [InlineData("a%20b.png", @"C:\docs\a b.png")]
        [InlineData("/assets/a.png", @"C:\assets\a.png")]
        [InlineData(@"\assets\a.png", @"C:\assets\a.png")]
        [InlineData("C:/pics/a.png", @"C:\pics\a.png")]
        [InlineData(@"C:\pics\a.png", @"C:\pics\a.png")]
        [InlineData("D:/other/a.png", @"D:\other\a.png")]
        public void Local_references_are_rewritten_to_image_host(string reference, string expectedPath)
        {
            string? url = LocalImageUrls.RewriteRelative(reference, @"C:\docs");

            Assert.Equal(LocalImageUrls.FromPath(expectedPath), url);
        }

        [Theory]
        [InlineData("https://example.com/a.png")]
        [InlineData("data:image/png;base64,AA")]
        [InlineData("#a")]
        [InlineData(@"\\server\share\a.png")]
        [InlineData("//cdn.example.com/a.png")]
        [InlineData("file:///C:/a.png")]
        [InlineData("")]
        public void Non_relative_references_are_not_rewritten(string reference)
        {
            Assert.Null(LocalImageUrls.RewriteRelative(reference, @"C:\docs"));
        }
    }
}
