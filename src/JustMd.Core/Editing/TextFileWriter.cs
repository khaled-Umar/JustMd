using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JustMd.Core.Editing
{
    /// <summary>Writes files so that, where the file system allows it, a crash or full disk can never leave a half-written document.</summary>
    public static class TextFileWriter
    {
        /// <summary>
        /// Atomic where the file system allows it: writes to a temporary file in the target's
        /// folder, then swaps it in. On failure the target is untouched, the temporary file is
        /// removed and the exception propagates. The target is overwritten in place instead when
        /// a swap would break it (<see cref="MustWriteInPlace"/>) or the folder refuses new files.
        /// </summary>
        public static void Write(string path, byte[] bytes)
        {
            string target = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(target)
                ?? throw new ArgumentException("The path has no parent folder.", nameof(path));

            if (File.Exists(target) && MustWriteInPlace(target))
            {
                WriteInPlace(target, bytes);
                return;
            }

            string temp = Path.Combine(directory, ".justmd-" + Guid.NewGuid().ToString("N") + ".tmp");
            FileStream stream;
            try
            {
                stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (UnauthorizedAccessException)
            {
                // A temporary-name collision is an IOException and stays an error.
                WriteInPlace(target, bytes);
                return;
            }

            try
            {
                using (stream)
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

        /// <summary>A swap would turn a symlink or cloud placeholder into a plain file, or split a hard link from its other names.</summary>
        private static bool MustWriteInPlace(string target) =>
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 || HasMultipleLinks(target);

        /// <summary>False when the file cannot be opened: the write that follows reports that itself.</summary>
        internal static bool HasMultipleLinks(string path)
        {
            try
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(handle, out ByHandleFileInformation information) && information.NumberOfLinks > 1;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void WriteInPlace(string target, byte[] bytes)
        {
            // Not FileMode.Create: Windows refuses to overwrite a hidden file that way.
            using var stream = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
