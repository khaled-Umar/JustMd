using System.Linq;
using JustMd.Core;

namespace JustMd.Tests
{
    public class ContextMenuPolicyTests
    {
        [Theory]
        [InlineData("copy")]
        [InlineData("selectAll")]
        public void Copy_and_select_all_are_always_kept(string name)
        {
            Assert.True(ContextMenuPolicy.Keeps(name, editable: false));
            Assert.True(ContextMenuPolicy.Keeps(name, editable: true));
        }

        [Theory]
        [InlineData("cut")]
        [InlineData("paste")]
        [InlineData("undo")]
        [InlineData("redo")]
        public void Editing_items_are_kept_only_when_editable(string name)
        {
            Assert.True(ContextMenuPolicy.Keeps(name, editable: true));
            Assert.False(ContextMenuPolicy.Keeps(name, editable: false));
        }

        [Theory]
        [InlineData("saveAs")]
        [InlineData("inspectElement")]
        [InlineData("emoji")]
        [InlineData("writingDirection")]
        [InlineData("spellcheck")]
        [InlineData("spellCheck")]
        [InlineData("addToDictionary")]
        [InlineData("")]
        public void Other_items_are_never_kept(string name)
        {
            Assert.False(ContextMenuPolicy.Keeps(name, editable: true));
            Assert.False(ContextMenuPolicy.Keeps(name, editable: false));
        }

        // A string of S (separator) and x (anything else) stands for a menu.
        [Theory]
        [InlineData("", new int[0])]
        [InlineData("x", new int[0])]
        [InlineData("xSx", new int[0])]
        [InlineData("SxS", new[] { 2, 0 })]
        [InlineData("S", new[] { 0 })]
        [InlineData("SSS", new[] { 2, 1, 0 })]
        [InlineData("xSSx", new[] { 2 })]
        [InlineData("xSS", new[] { 2, 1 })]
        [InlineData("SSx", new[] { 1, 0 })]
        [InlineData("SSxSSSxSS", new[] { 8, 7, 5, 4, 1, 0 })]
        public void Redundant_separators_are_listed_highest_first(string menu, int[] expected)
        {
            Assert.Equal(expected, ContextMenuPolicy.RedundantSeparators(Separators(menu)));
        }

        [Theory]
        [InlineData("SxS", "x")]
        [InlineData("xSSx", "xSx")]
        [InlineData("S", "")]
        [InlineData("xSS", "x")]
        [InlineData("SSxSSSxSS", "xSx")]
        public void Removing_in_the_given_order_leaves_a_clean_menu(string menu, string expected)
        {
            var items = menu.ToList();

            foreach (int index in ContextMenuPolicy.RedundantSeparators(Separators(menu)))
            {
                items.RemoveAt(index);
            }

            Assert.Equal(expected, new string(items.ToArray()));
        }

        private static bool[] Separators(string menu) => menu.Select(c => c == 'S').ToArray();
    }
}
