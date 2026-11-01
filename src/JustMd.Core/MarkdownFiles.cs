using System;
using System.Collections.Generic;
using System.IO;

namespace JustMd.Core
{
    /// <summary>Knows which file extensions are treated as Markdown documents.</summary>
    public static class MarkdownFiles
    {
        private static readonly HashSet<string> ExtensionSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

        /// <summary>The supported Markdown extensions, including the leading dot.</summary>
        public static IReadOnlyCollection<string> Extensions => ExtensionSet;

        /// <summary>Filter string for a Windows open-file dialog.</summary>
        public const string DialogFilter =
            "Markdown files (*.md;*.markdown;*.mdown;*.mkd)|*.md;*.markdown;*.mdown;*.mkd|Text files (*.txt)|*.txt|All files (*.*)|*.*";

        public static bool IsMarkdownPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                return ExtensionSet.Contains(Path.GetExtension(path));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
