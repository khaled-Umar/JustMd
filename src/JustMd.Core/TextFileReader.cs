using System.Globalization;
using System.IO;
using System.Text;

namespace JustMd.Core
{
    /// <summary>Reads text files with BOM detection, defaulting to UTF-8.</summary>
    public static class TextFileReader
    {
        /// <summary>Largest file the viewer will load (50 MB); bigger files are almost certainly not Markdown.</summary>
        public const long MaxFileBytes = 50L * 1024 * 1024;

        public static string ReadAllText(string path) => ReadAllText(path, out _);

        public static string ReadAllText(string path, out Encoding encoding)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                }

                bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset != bytes.Length)
                {
                    System.Array.Resize(ref bytes, offset);
                }
            }

            return Decode(bytes, out encoding);
        }

        public static string Decode(byte[] bytes, out Encoding encoding)
        {
            int bomLength = DetectBom(bytes, out Encoding? bomEncoding);
            if (bomEncoding != null)
            {
                encoding = bomEncoding;
                return bomEncoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            }

            try
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Not valid UTF-8: fall back to the system ANSI code page (what Notepad would do).
                encoding = GetAnsiEncoding();
                return encoding.GetString(bytes);
            }
        }

        private static Encoding GetAnsiEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return Encoding.GetEncoding(codePage > 0 ? codePage : 1252);
            }
            catch (System.Exception ex) when (ex is System.ArgumentException || ex is System.NotSupportedException)
            {
                return Encoding.Latin1;
            }
        }

        private static int DetectBom(byte[] b, out Encoding? encoding)
        {
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            {
                encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                return 4;
            }

            if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            {
                encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                return 4;
            }

            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                return 3;
            }

            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                return 2;
            }

            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                return 2;
            }

            encoding = null;
            return 0;
        }
    }
}
