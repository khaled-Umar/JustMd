using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustMd.Core.Editing;

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
        internal DocumentTab(int id, string? path, int untitledNumber = 0)
        {
            Id = id;
            Path = path;
            UntitledNumber = untitledNumber;
        }

        public int Id { get; }

        /// <summary>Normalised full path; null while the tab is untitled.</summary>
        public string? Path { get; internal set; }

        public bool IsUntitled => Path == null;

        /// <summary>The N of "Untitled N"; 0 for a tab opened from a file.</summary>
        public int UntitledNumber { get; }

        /// <summary>File name of <see cref="Path"/>; empty while the tab is untitled.</summary>
        public string FileName => System.IO.Path.GetFileName(Path) ?? string.Empty;

        public string DisplayName => IsUntitled ? "Untitled " + UntitledNumber : FileName;

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

        public bool IsEditing { get; set; }

        /// <summary>The page reports unsaved changes in the editor.</summary>
        public bool IsDirty { get; set; }

        /// <summary>The file changed on disk while there were unsaved changes.</summary>
        public bool ExternalChangePending { get; set; }

        /// <summary>Incremented each time the editor is rebuilt from disk, so the page can tell whether its state is current.</summary>
        public int EditRevision { get; set; }

        /// <summary>The file as last read for editing or saved.</summary>
        public DocumentText? Document { get; set; }

        /// <summary>The text last written, to recognise the watcher event caused by our own save.</summary>
        public string? LastSavedText { get; set; }

        /// <summary>The file was above the editing size limit when last read.</summary>
        public bool TooLargeToEdit { get; set; }
    }

    /// <summary>Ordered set of open documents with one active tab.</summary>
    public sealed class TabCollection
    {
        private readonly List<DocumentTab> _tabs = new();
        private int _nextId = 1;
        private int _nextUntitledNumber = 1;

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

        /// <summary>Adds a tab with no file at the end. Numbers are not reused within a session. Does not change the active tab.</summary>
        public DocumentTab NewUntitled()
        {
            var tab = new DocumentTab(_nextId++, null, _nextUntitledNumber++);
            _tabs.Add(tab);
            return tab;
        }

        /// <summary>
        /// Gives <paramref name="tab"/> a new path (first save or Save As). Another tab already
        /// showing that file is removed and returned as <paramref name="displaced"/>; the caller
        /// disposes its watcher. Returns false, changing nothing, for an invalid path.
        /// </summary>
        public bool SetPath(DocumentTab tab, string path, out DocumentTab? displaced)
        {
            displaced = null;
            if (!_tabs.Contains(tab))
            {
                throw new ArgumentException("The tab is not part of this collection.", nameof(tab));
            }

            string? normalized = DocumentPaths.Normalize(path);
            if (normalized == null)
            {
                return false;
            }

            DocumentTab? other = Find(normalized);
            if (other != null && !ReferenceEquals(other, tab))
            {
                Close(other);
                displaced = other;
            }

            tab.Path = normalized;
            return true;
        }

        /// <summary>The tab showing <paramref name="path"/>. Untitled tabs have no path, so are never returned.</summary>
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
