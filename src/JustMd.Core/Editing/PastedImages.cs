using System;
using System.Globalization;
using System.IO;

namespace JustMd.Core.Editing
{
    /// <summary>Stores images pasted into a document in a folder beside it, named after it.</summary>
    public static class PastedImages
    {
        public const string Extension = ".png";

        /// <summary>Upper bound for the names tried before <see cref="Save"/> gives up.</summary>
        public const int MaxAttempts = 1000;

        private const string FallbackFolder = "images";

        /// <summary><c>C:\docs\notes</c> for <c>C:\docs\notes.md</c>; <c>&lt;dir&gt;\images</c> when the name without extension is empty or is the document's own name.</summary>
        public static string FolderFor(string documentPath)
        {
            string document = Path.GetFullPath(documentPath);
            string directory = Path.GetDirectoryName(document)
                ?? throw new ArgumentException("The path has no parent folder.", nameof(documentPath));

            // Windows drops trailing dots and spaces from folder names, so "notes .md" must not ask
            // for "notes " and "...md" must not resolve to the parent folder.
            string name = Path.GetFileNameWithoutExtension(document).TrimEnd('.', ' ');
            string folder = Path.Combine(directory, name.Length == 0 ? FallbackFolder : name);

            // A document without an extension sits exactly where its folder would go.
            return string.Equals(folder, document, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directory, FallbackFolder)
                : folder;
        }

        /// <summary><c>image-20261104-153012.png</c>; attempt 2 gives <c>image-20261104-153012-2.png</c>.</summary>
        public static string FileName(DateTime timestamp, int attempt = 1)
        {
            string stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return attempt <= 1
                ? "image-" + stamp + Extension
                : "image-" + stamp + "-" + attempt.ToString(CultureInfo.InvariantCulture) + Extension;
        }

        /// <summary>
        /// Creates the folder and writes the image under the first free name, never over an existing
        /// file. Returns the full path. IO and access exceptions propagate.
        /// </summary>
        public static string Save(string documentPath, byte[] bytes, DateTime timestamp)
        {
            string folder = FolderFor(documentPath);
            Directory.CreateDirectory(folder);

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string candidate = Path.Combine(folder, FileName(timestamp, attempt));
                if (File.Exists(candidate))
                {
                    continue;
                }

                FileStream stream;
                try
                {
                    stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another paste took the name after the check above.
                    continue;
                }

                try
                {
                    using (stream)
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(flushToDisk: true);
                    }
                }
                catch
                {
                    TryDelete(candidate); // no truncated image left behind
                    throw;
                }

                return candidate;
            }

            throw new IOException("No free image name in " + folder + ".");
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The write failure is what the caller needs to hear about.
            }
        }

        /// <summary>True when the bytes start with the 8-byte PNG signature.</summary>
        public static bool IsPng(ReadOnlySpan<byte> bytes) =>
            bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }
}
