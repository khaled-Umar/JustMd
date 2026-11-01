using System.Collections.Generic;

namespace JustMd.Core
{
    /// <summary>Decides what stays in the page's context menu. Item names are WebView2's own.</summary>
    public static class ContextMenuPolicy
    {
        public static bool Keeps(string name, bool editable) =>
            name is "copy" or "selectAll"
            || (editable && name is "cut" or "paste" or "undo" or "redo");

        /// <summary>
        /// Indexes of the leading, trailing and repeated separators, highest first so they can be
        /// passed straight to RemoveAt. What remains never starts, ends or doubles up on a separator.
        /// </summary>
        public static IEnumerable<int> RedundantSeparators(IReadOnlyList<bool> isSeparator)
        {
            int first = 0;
            while (first < isSeparator.Count && isSeparator[first])
            {
                first++;
            }

            int last = isSeparator.Count - 1;
            while (last >= 0 && isSeparator[last])
            {
                last--;
            }

            var redundant = new List<int>();
            for (int i = isSeparator.Count - 1; i >= 0; i--)
            {
                // Past "first", a separator always has an item before it, so i - 1 is in range.
                if (isSeparator[i] && (i < first || i > last || isSeparator[i - 1]))
                {
                    redundant.Add(i);
                }
            }

            return redundant;
        }
    }
}
