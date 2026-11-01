using System;
using System.IO;

namespace JustMd.Core.Editing
{
    /// <summary>Writes files so a crash or full disk can never leave a half-written document.</summary>
    public static class TextFileWriter
    {
        /// <summary>
        /// Writes to a temporary file in the target's folder, then swaps it in. On failure the
        /// target is untouched, the temporary file is removed and the exception propagates.
        /// </summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            string target = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(target)
                ?? throw new ArgumentException("The path has no parent folder.", nameof(path));
            string temp = Path.Combine(directory, ".justmd-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(target))
                {
                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, target);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
