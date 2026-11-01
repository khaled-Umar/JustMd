using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JustMd.Core
{
    /// <summary>
    /// Maps local image files to URLs on the dedicated image virtual host and back.
    /// <para>
    /// URL shape: <c>https://img.justmd.example/f/C%3A/docs/img/a.png</c> for drive paths and
    /// <c>https://img.justmd.example/f/UNC/server/share/a.png</c> for UNC paths.
    /// </para>
    /// The host only ever serves files that pass <see cref="IsServable"/>: image extensions only,
    /// on the same drive/share as the open document.
    /// </summary>
    public static class LocalImageUrls
    {
        public const string Prefix = "https://" + VirtualHosts.Images + "/f/";

        private const string UncMarker = "UNC";

        private static readonly Regex DriveSegment = new Regex("^[A-Za-z]:$", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> MimeTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".png"] = "image/png",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".gif"] = "image/gif",
                [".webp"] = "image/webp",
                [".bmp"] = "image/bmp",
                [".ico"] = "image/x-icon",
                [".svg"] = "image/svg+xml",
                [".avif"] = "image/avif",
            };

        public static bool IsImageExtension(string path) => GetMimeType(path) != null;

        /// <summary>Returns the MIME type for an allowed image extension, or null for anything else.</summary>
        public static string? GetMimeType(string path)
        {
            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return null;
            }

            return MimeTypes.TryGetValue(extension, out string? mime) ? mime : null;
        }

        /// <summary>
        /// Rewrites a local image reference to an image-host URL: document-relative
        /// (<c>img/a.png</c>, <c>../a.png</c>), root-relative (<c>/img/a.png</c>, resolved on the
        /// document's drive) or drive-absolute (<c>C:/pics/a.png</c>). Returns null for anything
        /// else (URLs with a scheme, data URIs, anchors, network paths), which is left alone.
        /// Whether the file is actually served is decided later by <see cref="IsServable"/>.
        /// </summary>
        public static string? RewriteRelative(string? reference, string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrEmpty(baseDirectory))
            {
                return null;
            }

            string trimmed = reference.Trim();
            if (trimmed[0] == '#' || LocalPaths.IsNetworkOrProtocolRelative(trimmed) || LocalPaths.GetScheme(trimmed) != null)
            {
                return null;
            }

            string pathPart = LocalPaths.StripQueryAndFragment(trimmed, out _);
            if (pathPart.Length == 0)
            {
                return null;
            }

            string? full = LocalPaths.Resolve(pathPart, baseDirectory);
            return full == null ? null : FromPath(full);
        }

        /// <summary>Builds the image-host URL for an absolute local path.</summary>
        public static string FromPath(string fullPath)
        {
            IEnumerable<string> segments = LocalPaths.IsUncPath(fullPath)
                ? new[] { UncMarker }.Concat(fullPath.Substring(2).Split('\\'))
                : fullPath.Split('\\');

            return Prefix + string.Join("/", segments.Select(Uri.EscapeDataString));
        }

        /// <summary>
        /// URL of a directory with a trailing slash, used by the page to resolve relative
        /// <c>&lt;img src&gt;</c> found in raw HTML.
        /// </summary>
        public static string DirectoryBaseUrl(string directory) => FromPath(directory.TrimEnd('\\')) + "/";

        /// <summary>Decodes an image-host URL back to a full local path. Does not check that it is servable.</summary>
        public static bool TryGetPath(string? url, out string path)
        {
            path = string.Empty;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, VirtualHosts.Images, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string absolutePath = uri.AbsolutePath;
            if (!absolutePath.StartsWith("/f/", StringComparison.Ordinal))
            {
                return false;
            }

            string[] rawSegments = absolutePath.Substring(3).Split('/');
            var segments = new List<string>(rawSegments.Length);
            foreach (string raw in rawSegments)
            {
                string segment = LocalPaths.SafeUnescape(raw);
                if (segment.Length == 0 || segment == "." || segment == ".."
                    || segment.IndexOf('\\') >= 0 || segment.IndexOf('/') >= 0 || segment.IndexOf('\0') >= 0)
                {
                    return false;
                }

                segments.Add(segment);
            }

            string candidate;
            if (segments.Count >= 4 && segments[0] == UncMarker)
            {
                candidate = @"\\" + string.Join("\\", segments.Skip(1));
            }
            else if (segments.Count >= 2 && DriveSegment.IsMatch(segments[0]))
            {
                candidate = string.Join("\\", segments);
            }
            else
            {
                return false;
            }

            string? full = LocalPaths.TryGetFullPath(candidate);
            if (full == null || !LocalPaths.IsOrdinaryPath(full))
            {
                return false;
            }

            path = full;
            return true;
        }

        /// <summary>
        /// Policy check for serving a local file through the image host: it must have an image
        /// extension and live on the same drive or UNC share as the open document. Existence is
        /// checked by the caller.
        /// </summary>
        public static bool IsServable(string fullPath, string? documentPath)
        {
            if (string.IsNullOrEmpty(documentPath) || !LocalPaths.IsOrdinaryPath(fullPath) || !IsImageExtension(fullPath))
            {
                return false;
            }

            string? documentFull = LocalPaths.TryGetFullPath(documentPath!);
            return documentFull != null && LocalPaths.HaveSameRoot(fullPath, documentFull);
        }
    }
}
