using System;
using System.IO;

namespace JustMd.Core.Tabs
{
    /// <summary>Normalises document paths so the same file always maps to the same tab.</summary>
    public static class DocumentPaths
    {
        /// <summary>
        /// Full, normalised path (relative segments resolved, "file:" URIs and surrounding quotes
        /// removed, trailing separators trimmed). Returns null for empty or invalid input.
        /// </summary>
        public static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string value = path.Trim().Trim('"');
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                value = uri.LocalPath;
            }

            if (value.Length == 0 || value.IndexOf('\0') >= 0)
            {
                return null;
            }

            string? full = LocalPaths.TryGetFullPath(value);
            if (full == null)
            {
                return null;
            }

            string? root = LocalPaths.SafeGetPathRoot(full);
            return root != null && full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }

        /// <summary>Windows paths are case-insensitive.</summary>
        public static bool AreSame(string? a, string? b)
        {
            string? na = Normalize(a);
            string? nb = Normalize(b);
            return na != null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
    }
}
