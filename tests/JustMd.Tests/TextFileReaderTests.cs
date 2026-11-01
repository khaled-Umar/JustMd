using System.IO;
using System.Text;
using JustMd.Core;

namespace JustMd.Tests
{
    public class TextFileReaderTests
    {
        private const string Sample = "# Café — 日本語 \U0001F600";

        [Fact]
        public void Reads_utf8_without_bom()
        {
            AssertRoundTrip(new UTF8Encoding(false), "utf-8");
        }

        [Fact]
        public void Reads_utf8_with_bom_and_strips_it()
        {
            AssertRoundTrip(new UTF8Encoding(true), "utf-8");
        }

        [Fact]
        public void Reads_utf16_le_and_be()
        {
            AssertRoundTrip(new UnicodeEncoding(false, true), "utf-16");
            AssertRoundTrip(new UnicodeEncoding(true, true), "utf-16BE");
        }

        [Fact]
        public void Reads_utf32()
        {
            AssertRoundTrip(new UTF32Encoding(false, true), "utf-32");
        }

        [Fact]
        public void Invalid_utf8_falls_back_without_throwing()
        {
            byte[] bytes = { (byte)'a', 0xE9, (byte)'b' }; // "aéb" in Windows-1252

            string text = TextFileReader.Decode(bytes, out Encoding encoding);

            Assert.Equal(3, text.Length);
            Assert.StartsWith("a", text);
            Assert.NotEqual("utf-8", encoding.WebName);
        }

        [Fact]
        public void Reads_file_that_is_open_for_writing_elsewhere()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("doc.md", "hello");

            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.Equal("hello", TextFileReader.ReadAllText(path));
        }

        private static void AssertRoundTrip(Encoding encoding, string expectedWebName)
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("doc.md");
            File.WriteAllText(path, Sample, encoding);

            string text = TextFileReader.ReadAllText(path, out Encoding detected);

            Assert.Equal(Sample, text);
            Assert.Equal(expectedWebName, detected.WebName);
        }
    }
}
