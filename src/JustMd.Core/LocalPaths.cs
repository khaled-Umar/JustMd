using System;
using System.IO;
using System.Text.RegularExpressions;

namespace JustMd.Core
{
    /// <summary>Helpers for turning link and image references into validated local paths.</summary>
    internal static class LocalPaths
    {
        private static readonly Regex SchemePattern =
            new Regex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):", RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns the URL scheme of <paramref name="reference"/> in lower case, or null when it has none.
        /// A single letter followed by a colon is a Windows drive letter, not a scheme.
        /// </summary>
        public static string? GetScheme(string reference)
        {
            Match match = SchemePattern.Match(reference);
            if (!match.Success)
            {
                return null;
            }

            string scheme = match.Groups["scheme"].Value;
            return scheme.Length == 1 ? null : scheme.ToLowerInvariant();
        }

        public static bool IsDriveAbsolute(string reference) =>
            reference.Length >= 3
            && char.IsLetter(reference[0]) && reference[0] < 128
            && reference[1] == ':'
            && (reference[2] == '\\' || reference[2] == '/');

        /// <summary>True for references like <c>//host/x</c> or <c>\\server\share</c>.</summary>
        public static bool IsNetworkOrProtocolRelative(string reference) =>
            reference.StartsWith("//", StringComparison.Ordinal)
            || reference.StartsWith(@"\\", StringComparison.Ordinal)
            || reference.StartsWith(@"/\", StringComparison.Ordinal)
            || reference.StartsWith(@"\/", StringComparison.Ordinal);

        /// <summary>Splits "path?query#fragment" into the path and the (decoded) fragment.</summary>
        public static string StripQueryAndFragment(string reference, out string? fragment)
        {
            fragment = null;
            int hash = reference.IndexOf('#');
            if (hash >= 0)
            {
                fragment = SafeUnescape(reference.Substring(hash + 1));
                reference = reference.Substring(0, hash);
            }

            int query = reference.IndexOf('?');
            if (query >= 0)
            {
                reference = reference.Substring(0, query);
            }

            return reference;
        }

        public static string SafeUnescape(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        /// <summary>
        /// Resolves a local (relative, root-relative or drive-absolute) reference against a base
        /// directory. Returns null when the result is not a usable, ordinary file-system path.
        /// </summary>
        public static string? Resolve(string reference, string baseDirectory)
        {
            string path = SafeUnescape(reference).Replace('/', '\\');
            if (path.Length == 0 || path.IndexOf('\0') >= 0)
            {
                return null;
            }

            string combined;
            if (IsDriveAbsolute(path))
            {
                combined = path;
            }
            else if (path[0] == '\\')
            {
                string? root = SafeGetPathRoot(baseDirectory);
                if (string.IsNullOrEmpty(root))
                {
                    return null;
                }

                combined = root!.TrimEnd('\\') + path;
            }
            else
            {
                combined = baseDirectory.TrimEnd('\\') + "\\" + path;
            }

            string? full = TryGetFullPath(combined);
            return full != null && IsOrdinaryPath(full) ? full : null;
        }

        public static string? TryGetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                       || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        public static string? SafeGetPathRoot(string path)
        {
            try
            {
                return Path.GetPathRoot(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Rejects device paths (<c>\\?\</c>, <c>\\.\</c>) and NTFS alternate data streams
        /// (any colon other than the drive-letter colon).
        /// </summary>
        public static bool IsOrdinaryPath(string fullPath)
        {
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return false;
            }

            int colon = fullPath.IndexOf(':');
            if (colon >= 0 && (colon != 1 || fullPath.IndexOf(':', 2) >= 0))
            {
                return false;
            }

            return fullPath.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }

        public static bool IsUncPath(string fullPath) => fullPath.StartsWith(@"\\", StringComparison.Ordinal);

        /// <summary>True when both paths share the same drive or UNC share root.</summary>
        public static bool HaveSameRoot(string a, string b)
        {
            string? rootA = SafeGetPathRoot(a);
            string? rootB = SafeGetPathRoot(b);
            return !string.IsNullOrEmpty(rootA)
                && string.Equals(rootA!.TrimEnd('\\'), rootB?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
