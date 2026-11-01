using System.IO;

namespace JustMd.Core.Editing
{
    /// <summary>Builds the Markdown reference for an image the user picked or dropped.</summary>
    public static class RelativeReferences
    {
        /// <summary>
        /// Relative to the document's folder when both are on the same drive or share; otherwise
        /// (or for an untitled document) absolute. Uses forward slashes and is percent-encoded so
        /// <see cref="LocalImageUrls.RewriteRelative"/> resolves it back to the same file.
        /// </summary>
        public static string ForImage(string? documentPath, string imagePath)
        {
            string image = LocalPaths.TryGetFullPath(imagePath) ?? imagePath;
            string? document = string.IsNullOrEmpty(documentPath) ? null : LocalPaths.TryGetFullPath(documentPath!);
            string? directory = document == null ? null : Path.GetDirectoryName(document);

            string reference = directory != null && LocalPaths.HaveSameRoot(image, directory)
                ? Path.GetRelativePath(directory, image)
                : image;

            return Encode(reference.Replace('\\', '/'));
        }

        /// <summary>
        /// Encodes only what would break the reference: spaces and parentheses end a Markdown link
        /// destination, "%" and "&amp;" would be decoded, "#" would start a fragment. Other
        /// characters (Arabic file names, for one) stay readable.
        /// </summary>
        private static string Encode(string reference) =>
            reference
                .Replace("%", "%25")
                .Replace(" ", "%20")
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("#", "%23")
                .Replace("&", "%26");
    }
}
