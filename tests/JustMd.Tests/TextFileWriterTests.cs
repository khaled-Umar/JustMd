using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd.Tests
{
    public class TextFileWriterTests
    {
        [Fact]
        public void Creates_a_new_file_with_exact_bytes()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("new.md");
            byte[] bytes = { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' };

            TextFileWriter.Write(path, bytes);

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void Overwrites_an_existing_file_and_leaves_no_temporary_file()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            byte[] bytes = { (byte)'b' };

            TextFileWriter.Write(path, bytes);

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void Failure_keeps_the_original_and_removes_the_temporary_file()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "original");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                Assert.ThrowsAny<Exception>(() => TextFileWriter.Write(path, new byte[] { 1 }));

                Assert.Equal("original", File.ReadAllText(path));
                Assert.Single(Directory.GetFiles(temp.Path));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        [Fact]
        public void Missing_folder_throws()
        {
            using var temp = new TempDirectory();

            Assert.Throws<DirectoryNotFoundException>(() => TextFileWriter.Write(temp.Combine("none", "a.md"), new byte[] { 1 }));
        }

        [Fact]
        public void A_hard_link_keeps_both_names_on_the_new_bytes()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            string other = temp.Combine("b.md");
            CreateHardLink(other, path);
            byte[] bytes = { (byte)'n', (byte)'e', (byte)'w' };

            TextFileWriter.Write(path, bytes);

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(other));
            Assert.Empty(Directory.GetFiles(temp.Path, ".justmd-*.tmp"));
            Assert.True(TextFileWriter.HasMultipleLinks(path));
        }

        [Fact]
        public void An_ordinary_file_has_one_link()
        {
            using var temp = new TempDirectory();

            Assert.False(TextFileWriter.HasMultipleLinks(temp.WriteFile("a.md", "x")));
            Assert.False(TextFileWriter.HasMultipleLinks(temp.Combine("missing.md")));
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public void A_folder_that_refuses_new_files_gets_the_target_rewritten()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            byte[] bytes = { (byte)'n', (byte)'e', (byte)'w' };

            using (DenyCreatingFiles(temp.Path))
            {
                TextFileWriter.Write(path, bytes);
            }

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(temp.Path));
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public void A_folder_that_refuses_new_files_cannot_get_a_new_target()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");

            using (DenyCreatingFiles(temp.Path))
            {
                Assert.Throws<UnauthorizedAccessException>(() => TextFileWriter.Write(path, new byte[] { 1 }));
            }

            Assert.Empty(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void A_hidden_hard_linked_file_is_rewritten_and_stays_hidden()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            string other = temp.Combine("b.md");
            CreateHardLink(other, path);
            File.SetAttributes(path, FileAttributes.Hidden);
            byte[] bytes = { (byte)'n', (byte)'e', (byte)'w' };

            TextFileWriter.Write(path, bytes);

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(other));
            Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) != 0);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public void A_hidden_file_in_a_folder_that_refuses_new_files_is_rewritten()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            File.SetAttributes(path, FileAttributes.Hidden);
            byte[] bytes = { (byte)'n', (byte)'e', (byte)'w' };

            using (DenyCreatingFiles(temp.Path))
            {
                TextFileWriter.Write(path, bytes);
            }

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) != 0);
        }

        [Fact]
        public void An_in_place_write_of_a_read_only_file_fails_and_keeps_it()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "original");
            CreateHardLink(temp.Combine("b.md"), path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => TextFileWriter.Write(path, new byte[] { 1 }));

                Assert.Equal("original", File.ReadAllText(path));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        /// <summary>The host's LastSavedText (Compose at save) must equal the next read, or it sees its own save as a change.</summary>
        [Theory]
        [InlineData("utf-8")]
        [InlineData("utf-8-bom")]
        [InlineData("utf-16le")]
        [InlineData("utf-16be")]
        [InlineData("utf-32le")]
        [InlineData("utf-32be")]
        public void Composed_text_is_what_the_reader_reads_back(string encodingName)
        {
            Encoding encoding = encodingName switch
            {
                "utf-8" => new UTF8Encoding(false),
                "utf-8-bom" => new UTF8Encoding(true),
                "utf-16le" => new UnicodeEncoding(false, true),
                "utf-16be" => new UnicodeEncoding(true, true),
                "utf-32le" => new UTF32Encoding(false, true),
                _ => new UTF32Encoding(true, true),
            };
            byte[] file = encoding.GetPreamble().Concat(encoding.GetBytes(MixedDocument)).ToArray();

            AssertSavesReadBack(file);
        }

        [Fact]
        public void Composed_text_is_what_the_reader_reads_back_in_a_legacy_code_page()
        {
            // Not valid UTF-8, so the reader falls back to the system code page.
            AssertSavesReadBack(new byte[] { 0x63, 0xE9, (byte)'\r', (byte)'\n', (byte)'o', (byte)'l', (byte)'d', (byte)'\n', (byte)'z', 0xE9 });
        }

        [Fact]
        public void Composed_text_after_falling_back_to_utf8_is_what_the_reader_reads_back()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");
            DocumentText document = DocumentText.Parse(MixedDocument, new UnicodeEncoding(false, true)).With("مرحبا\n\nx", true).AsUtf8();

            TextFileWriter.Write(path, document.ToBytes());

            Assert.Equal(document.Compose(), TextFileReader.ReadAllText(path));
        }

        [Fact]
        public void Composed_text_through_a_hard_link_is_what_the_reader_reads_back()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(MixedDocument));
            CreateHardLink(temp.Combine("b.md"), path);
            DocumentText document = DocumentText.Parse(TextFileReader.ReadAllText(path, out Encoding encoding), encoding);

            TextFileWriter.Write(path, document.ToBytes());

            Assert.Equal(MixedDocument, document.Compose());
            Assert.Equal(document.Compose(), TextFileReader.ReadAllText(path));
        }

        [Theory]
        [InlineData("\uFEFFy")]
        [InlineData("\uFEFF\uFEFFy")]
        [InlineData("\uFEFF")]
        public void Composed_text_starting_with_a_bom_character_is_what_the_reader_reads_back(string typed)
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");
            DocumentText document = DocumentText.Parse("x\n", new UTF8Encoding(false)).With(typed, false);

            Assert.True(document.SurvivesEncoding());
            TextFileWriter.Write(path, document.ToBytes());

            Assert.Equal(document.Compose(), TextFileReader.ReadAllText(path));
        }

        [Fact]
        public void Composed_text_starting_with_a_bom_character_after_a_preamble_is_what_the_reader_reads_back()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");
            DocumentText document = DocumentText.Parse("x\n", new UTF8Encoding(true)).With("\uFEFFy", false);

            TextFileWriter.Write(path, document.ToBytes());
            Assert.Equal(document.Compose(), TextFileReader.ReadAllText(path));

            DocumentText utf8 = document.AsUtf8();
            TextFileWriter.Write(path, utf8.ToBytes());
            Assert.Equal(utf8.Compose(), TextFileReader.ReadAllText(path));
        }

        [Fact]
        public void Legacy_bytes_that_are_valid_utf8_must_be_saved_as_utf8_to_read_back()
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                using var temp = new TempDirectory();
                string path = temp.Combine("a.md");
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                DocumentText document = DocumentText.Parse("x\n", Encoding.GetEncoding(1252)).With("\u00c3\u00a9", false);

                Assert.False(document.SurvivesEncoding());

                DocumentText utf8 = document.AsUtf8();
                Assert.True(utf8.SurvivesEncoding());
                TextFileWriter.Write(path, utf8.ToBytes());
                Assert.Equal(utf8.Compose(), TextFileReader.ReadAllText(path));
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void Legacy_text_the_check_lets_through_is_what_the_reader_reads_back()
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                using var temp = new TempDirectory();
                string path = temp.Combine("a.md");
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                DocumentText document = DocumentText.Parse("x\n", Encoding.GetEncoding(1252)).With("caf\u00e9", false);

                Assert.True(document.SurvivesEncoding());
                TextFileWriter.Write(path, document.ToBytes());

                Assert.Equal(document.Compose(), TextFileReader.ReadAllText(path));
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        private const string MixedDocument =
            "---\r\nt: x\n---\r\n\n<div dir=\"rtl\">\n\r\n# مرحبا\r\n\r\nold\nline\r\n\r\n</div>\r\n\n\n";

        /// <summary>Opens, saves unchanged, edits, saves again and reopens: each time the disk equals Compose.</summary>
        private static void AssertSavesReadBack(byte[] file)
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("a.md");
            File.WriteAllBytes(path, file);

            string read = TextFileReader.ReadAllText(path, out Encoding encoding);
            DocumentText opened = DocumentText.Parse(read, encoding);
            Assert.Equal(read, opened.Compose());

            TextFileWriter.Write(path, opened.ToBytes());
            Assert.Equal(file, File.ReadAllBytes(path));
            Assert.Equal(opened.Compose(), TextFileReader.ReadAllText(path));

            DocumentText edited = opened.With(opened.Body.Replace("old", "new") + "\n\nadded", opened.IsRtl);
            Assert.NotEqual(opened.Compose(), edited.Compose());
            TextFileWriter.Write(path, edited.ToBytes());

            string reread = TextFileReader.ReadAllText(path, out Encoding rereadEncoding);
            Assert.Equal(edited.Compose(), reread);
            Assert.Equal(encoding.WebName, rereadEncoding.WebName);
            Assert.Equal(edited.Compose(), edited.With(edited.Body, edited.IsRtl).Compose());
            Assert.Equal(reread, DocumentText.Parse(reread, rereadEncoding).Compose());
        }

        private static void CreateHardLink(string link, string existing)
        {
            if (!CreateHardLinkW(link, existing, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [SupportedOSPlatform("windows")]
        private static DenyCreateFilesRule DenyCreatingFiles(string folder) => new(new DirectoryInfo(folder));

        /// <summary>Adds a Deny CreateFiles ACE for the current user to the folder itself (files in it are not affected); dispose removes it.</summary>
        [SupportedOSPlatform("windows")]
        private sealed class DenyCreateFilesRule : IDisposable
        {
            private readonly DirectoryInfo _folder;
            private readonly FileSystemAccessRule _rule;

            public DenyCreateFilesRule(DirectoryInfo folder)
            {
                _folder = folder;
                using WindowsIdentity user = WindowsIdentity.GetCurrent();
                _rule = new FileSystemAccessRule(user.User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
                DirectorySecurity security = _folder.GetAccessControl();
                security.AddAccessRule(_rule);
                _folder.SetAccessControl(security);
            }

            public void Dispose()
            {
                DirectorySecurity security = _folder.GetAccessControl();
                security.RemoveAccessRule(_rule);
                _folder.SetAccessControl(security);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
    }
}
