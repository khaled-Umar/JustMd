using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd
{
    /// <summary>Reads images off the Windows clipboard. Call on the UI (STA) thread.</summary>
    internal static class ClipboardImages
    {
        /// <summary>The registered format browsers and screenshot tools offer next to the plain bitmap.</summary>
        private const string PngFormat = "PNG";

        /// <summary>
        /// PNG bytes: the "PNG" clipboard format when offered (it keeps transparency), else the
        /// clipboard bitmap re-encoded. Null when there is no image or the clipboard cannot be read.
        /// </summary>
        public static byte[]? TryGetPng()
        {
            try
            {
                return ReadPngFormat() ?? EncodeBitmap();
            }
            catch (Exception ex) when (IsClipboardFailure(ex))
            {
                return null;
            }
        }

        /// <summary>Image files copied in Explorer. Empty when there are none or the clipboard cannot be read.</summary>
        public static IReadOnlyList<string> GetImageFiles()
        {
            try
            {
                if (!Clipboard.ContainsFileDropList())
                {
                    return Array.Empty<string>();
                }

                StringCollection files = Clipboard.GetFileDropList();
                return files.Cast<string>().Where(LocalImageUrls.IsImageExtension).Where(File.Exists).ToList();
            }
            catch (Exception ex) when (IsClipboardFailure(ex))
            {
                return Array.Empty<string>();
            }
        }

        private static byte[]? ReadPngFormat()
        {
            if (!Clipboard.ContainsData(PngFormat) || !Clipboard.TryGetData(PngFormat, out MemoryStream? stream) || stream == null)
            {
                return null;
            }

            using (stream)
            {
                byte[] bytes = stream.ToArray();
                return PastedImages.IsPng(bytes) ? bytes : null;
            }
        }

        private static byte[]? EncodeBitmap()
        {
            if (!Clipboard.ContainsImage())
            {
                return null;
            }

            using Image? image = Clipboard.GetImage();
            if (image == null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        /// <summary>Another program holds the clipboard, or what it offers cannot be decoded (GDI+ reports a corrupt bitmap as out of memory).</summary>
        private static bool IsClipboardFailure(Exception ex) =>
            ex is ExternalException || ex is ThreadStateException || ex is ArgumentException || ex is InvalidOperationException || ex is OutOfMemoryException;
    }
}
