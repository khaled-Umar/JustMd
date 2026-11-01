using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JustMd.Core.Tabs
{
    /// <summary>What the page reported about a tab's view: document scroll and outline state.</summary>
    public sealed class TabViewState
    {
        public TabViewState(double scrollTop, double outlineScrollTop, IReadOnlyList<string> collapsedKeys)
        {
            ScrollTop = scrollTop;
            OutlineScrollTop = outlineScrollTop;
            CollapsedKeys = collapsedKeys;
        }

        public double ScrollTop { get; }

        public double OutlineScrollTop { get; }

        /// <summary>Keys (heading id / text path) of collapsed outline nodes.</summary>
        public IReadOnlyList<string> CollapsedKeys { get; }
    }

    /// <summary>One open document. UI-free; the host attaches its file watcher via <see cref="Watcher"/>.</summary>
    public sealed class DocumentTab
    {
        internal DocumentTab(int id, string path)
        {
            Id = id;
            Path = path;
        }

        public int Id { get; }

        /// <summary>Normalised full path.</summary>
        public string Path { get; }

        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>Last rendered (unsanitised) HTML, so switching back is instant.</summary>
        public string? CachedHtml { get; set; }

        /// <summary>Error to show instead of content when the last read failed.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Content changed on disk while the tab was in the background.</summary>
        public bool Updated { get; set; }

        public TabViewState? View { get; set; }

        /// <summary>Incremented per render request so stale results can be dropped.</summary>
        public int RenderVersion { get; set; }

        /// <summary>The host's file watcher for this tab (disposed when the tab closes).</summary>
        public IDisposable? Watcher { get; set; }
    }

    /// <summary>Ordered set of open documents with one active tab.</summary>
    public sealed class TabCollection
    {
        private readonly List<DocumentTab> _tabs = new();
        private int _nextId = 1;

        public IReadOnlyList<DocumentTab> Tabs => _tabs;

        public DocumentTab? Active { get; private set; }

        public int Count => _tabs.Count;

        public int ActiveIndex => Active == null ? -1 : _tabs.IndexOf(Active);

        /// <summary>
        /// Returns the tab for <paramref name="path"/>, adding it at the end if it is not open yet.
        /// Does not change the active tab. Returns null for an invalid path.
        /// </summary>
        public DocumentTab? Open(string path, out bool alreadyOpen)
        {
            alreadyOpen = false;
            string? normalized = DocumentPaths.Normalize(path);
            if (normalized == null)
            {
                return null;
            }

            DocumentTab? existing = Find(normalized);
            if (existing != null)
            {
                alreadyOpen = true;
                return existing;
            }

            var tab = new DocumentTab(_nextId++, normalized);
            _tabs.Add(tab);
            return tab;
        }

        public DocumentTab? Find(string? path)
        {
            string? normalized = DocumentPaths.Normalize(path);
            return normalized == null
                ? null
                : _tabs.FirstOrDefault(t => string.Equals(t.Path, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public DocumentTab? FindById(int id) => _tabs.FirstOrDefault(t => t.Id == id);

        public void Activate(DocumentTab tab)
        {
            if (!_tabs.Contains(tab))
            {
                throw new ArgumentException("The tab is not part of this collection.", nameof(tab));
            }

            Active = tab;
        }

        /// <summary>
        /// Removes a tab. If it was active, the tab to its right (or else its left) becomes active.
        /// Returns the active tab afterwards (null when no tabs remain).
        /// </summary>
        public DocumentTab? Close(DocumentTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return Active;
            }

            _tabs.RemoveAt(index);
            if (ReferenceEquals(Active, tab))
            {
                Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
            }

            return Active;
        }

        /// <summary>Tab <paramref name="delta"/> positions from the active one, wrapping around.</summary>
        public DocumentTab? Cycle(int delta)
        {
            if (_tabs.Count == 0)
            {
                return null;
            }

            int start = Math.Max(0, ActiveIndex);
            int index = ((start + delta) % _tabs.Count + _tabs.Count) % _tabs.Count;
            return _tabs[index];
        }

        /// <summary>Ctrl+1..8 select that position; Ctrl+9 selects the last tab (browser convention).</summary>
        public DocumentTab? ByNumber(int number)
        {
            if (_tabs.Count == 0 || number < 1 || number > 9)
            {
                return null;
            }

            if (number == 9)
            {
                return _tabs[_tabs.Count - 1];
            }

            return number <= _tabs.Count ? _tabs[number - 1] : null;
        }

        /// <summary>Moves a tab to a new position (drag reordering).</summary>
        public void Move(DocumentTab tab, int newIndex)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            _tabs.RemoveAt(index);
            _tabs.Insert(Math.Max(0, Math.Min(newIndex, _tabs.Count)), tab);
        }
    }
}
