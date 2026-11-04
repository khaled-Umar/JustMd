using System;
using System.Globalization;
using System.IO;
using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd.Tests
{
    public class PastedImagesTests
    {
        private const string FirstName = "image-20261104-153012.png";
        private const string SecondName = "image-20261104-153012-2.png";

        private static readonly DateTime Timestamp = new DateTime(2026, 11, 4, 15, 30, 12);

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        [Theory]
        [InlineData(@"C:\docs\notes.md", @"C:\docs\notes")]
        [InlineData(@"C:\docs\my notes.md", @"C:\docs\my notes")]
        [InlineData(@"C:\docs\ملاحظات.md", @"C:\docs\ملاحظات")]
        [InlineData(@"C:\docs\a.b.md", @"C:\docs\a.b")]
        [InlineData(@"\\server\share\d\a.md", @"\\server\share\d\a")]
        [InlineData(@"C:\docs\.md", @"C:\docs\images")]
        public void Folder_is_beside_the_document_and_named_after_it(string document, string expected)
        {
            Assert.Equal(expected, PastedImages.FolderFor(document));
        }

        [Theory]
        [InlineData(@"C:\docs\notes .md", @"C:\docs\notes")]
        [InlineData(@"C:\docs\notes..md", @"C:\docs\notes")]
        [InlineData(@"C:\docs\..md", @"C:\docs\images")]
        [InlineData(@"C:\docs\...md", @"C:\docs\images")]
        [InlineData(@"C:\docs\ .md", @"C:\docs\images")]
        public void Folder_name_has_no_trailing_dots_or_spaces(string document, string expected)
        {
            Assert.Equal(expected, PastedImages.FolderFor(document));
        }

        [Theory]
        [InlineData(@"C:\docs\notes", @"C:\docs\images")]
        [InlineData(@"C:\docs\NOTES", @"C:\docs\images")]
        [InlineData(@"C:\docs\notes.", @"C:\docs\images")]
        [InlineData(@"C:\docs\notes ", @"C:\docs\images")]
        public void Folder_of_a_document_without_an_extension_is_not_the_document_itself(string document, string expected)
        {
            Assert.Equal(expected, PastedImages.FolderFor(document));
        }

        [Fact]
        public void File_name_holds_the_timestamp_and_the_attempt()
        {
            Assert.Equal(FirstName, PastedImages.FileName(Timestamp));
            Assert.Equal(FirstName, PastedImages.FileName(Timestamp, 1));
            Assert.Equal(SecondName, PastedImages.FileName(Timestamp, 2));
        }

        [Fact]
        public void File_name_does_not_follow_the_current_culture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            try
            {
                Assert.Equal(SecondName, PastedImages.FileName(Timestamp, 2));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Save_creates_the_folder_and_writes_the_bytes()
        {
            using var temp = new TempDirectory();
            string document = temp.Combine("notes.md");

            string saved = PastedImages.Save(document, Png, Timestamp);

            Assert.Equal(temp.Combine("notes", FirstName), saved);
            Assert.Equal(Png, File.ReadAllBytes(saved));
        }

        [Fact]
        public void Second_save_with_the_same_timestamp_takes_the_next_name()
        {
            using var temp = new TempDirectory();
            string document = temp.Combine("notes.md");

            string first = PastedImages.Save(document, Png, Timestamp);
            string second = PastedImages.Save(document, new byte[] { 9 }, Timestamp);

            Assert.Equal(FirstName, Path.GetFileName(first));
            Assert.Equal(SecondName, Path.GetFileName(second));
            Assert.Equal(Png, File.ReadAllBytes(first));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(second));
        }

        [Fact]
        public void Save_skips_an_existing_file_and_leaves_it_unchanged()
        {
            using var temp = new TempDirectory();
            string existing = temp.WriteFile(Path.Combine("notes", FirstName), "keep");

            string saved = PastedImages.Save(temp.Combine("notes.md"), Png, Timestamp);

            Assert.Equal(SecondName, Path.GetFileName(saved));
            Assert.Equal("keep", File.ReadAllText(existing));
        }

        [Fact]
        public void Save_fails_when_a_file_sits_at_the_folder_path()
        {
            using var temp = new TempDirectory();
            temp.WriteFile("notes", "a file");

            Assert.ThrowsAny<IOException>(() => PastedImages.Save(temp.Combine("notes.md"), Png, Timestamp));
        }

        [Fact]
        public void Save_gives_up_when_every_name_is_taken()
        {
            using var temp = new TempDirectory();
            string folder = Directory.CreateDirectory(temp.Combine("notes")).FullName;
            for (int attempt = 1; attempt <= PastedImages.MaxAttempts; attempt++)
            {
                File.WriteAllBytes(Path.Combine(folder, PastedImages.FileName(Timestamp, attempt)), Array.Empty<byte>());
            }

            Assert.ThrowsAny<IOException>(() => PastedImages.Save(temp.Combine("notes.md"), Png, Timestamp));
        }

        [Fact]
        public void Save_works_when_the_document_name_ends_with_a_space()
        {
            using var temp = new TempDirectory();

            string saved = PastedImages.Save(temp.Combine("notes .md"), Png, Timestamp);

            Assert.Equal(temp.Combine("notes", FirstName), saved);
            Assert.Equal(Png, File.ReadAllBytes(saved));
        }

        [Fact]
        public void Save_works_for_a_document_without_an_extension()
        {
            using var temp = new TempDirectory();
            string document = temp.WriteFile("notes", "text");

            string saved = PastedImages.Save(document, Png, Timestamp);

            Assert.Equal(temp.Combine("images", FirstName), saved);
            Assert.Equal(Png, File.ReadAllBytes(saved));
            Assert.Equal("text", File.ReadAllText(document));
            Assert.Equal("images/" + FirstName, RelativeReferences.ForImage(document, saved));
        }

        [Fact]
        public void Failed_write_leaves_no_file_behind()
        {
            using var temp = new TempDirectory();

            // Null bytes fail after the file was created, as a full disk would.
            Assert.ThrowsAny<Exception>(() => PastedImages.Save(temp.Combine("notes.md"), null!, Timestamp));

            Assert.Empty(Directory.GetFiles(temp.Combine("notes")));
        }

        [Theory]
        [InlineData("notes.md", "notes/" + FirstName)]
        [InlineData("my notes.md", "my%20notes/" + FirstName)]
        public void Saved_image_gets_a_reference_the_viewer_resolves_and_serves(string documentName, string expected)
        {
            using var temp = new TempDirectory();
            string document = temp.Combine(documentName);

            string saved = PastedImages.Save(document, Png, Timestamp);
            string reference = RelativeReferences.ForImage(document, saved);

            Assert.Equal(expected, reference);
            Assert.Equal(LocalImageUrls.FromPath(saved), LocalImageUrls.RewriteRelative(reference, temp.Path));
            Assert.True(LocalImageUrls.IsServable(saved, document));
        }

        [Fact]
        public void Is_png_checks_the_signature()
        {
            Assert.True(PastedImages.IsPng(Png));
            Assert.True(PastedImages.IsPng(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.False(PastedImages.IsPng(Array.Empty<byte>()));
            Assert.False(PastedImages.IsPng(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A }));
            Assert.False(PastedImages.IsPng(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 }));
        }
    }
}
