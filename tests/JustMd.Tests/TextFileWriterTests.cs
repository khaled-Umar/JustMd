using System;
using System.IO;
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

            TextFileWriter.WriteAtomic(path, bytes);

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void Overwrites_an_existing_file_and_leaves_no_temporary_file()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("a.md", "a much longer original content");
            byte[] bytes = { (byte)'b' };

            TextFileWriter.WriteAtomic(path, bytes);

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
                Assert.ThrowsAny<Exception>(() => TextFileWriter.WriteAtomic(path, new byte[] { 1 }));

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

            Assert.Throws<DirectoryNotFoundException>(() => TextFileWriter.WriteAtomic(temp.Combine("none", "a.md"), new byte[] { 1 }));
        }
    }
}
