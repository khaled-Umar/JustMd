using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JustMd.Core;
using JustMd.Core.Messaging;
using JustMd.Core.Settings;
using JustMd.Core.Tabs;

namespace JustMd
{
    /// <summary>Tab model: opening, switching, closing, rendering and the remembered session.</summary>
    internal sealed partial class MainForm
    {
        private readonly TabCollection _tabs = new();

        private enum RenderMode
        {
            /// <summary>Tab is being shown: restore its view, or honour a fragment.</summary>
            Show,

            /// <summary>F5 on the active tab: keep scroll, report errors.</summary>
            Reload,

            /// <summary>File changed on disk, active tab: keep scroll, keep last content on error.</summary>
            AutoReload,

            /// <summary>File changed on disk, background tab: refresh the cache silently.</summary>
            Background,
        }

        // ---------- opening ----------

        /// <summary>Reopens the last session, then adds the command-line files (last one active).</summary>
        private void RestoreStartupTabs(IReadOnlyList<string> files)
        {
            (IReadOnlyList<StartupTab> tabs, int activeIndex) = StartupPlan.Build(_settings.Session, files, File.Exists);
            DocumentTab? active = null;
            for (int i = 0; i < tabs.Count; i++)
            {
                DocumentTab? tab = AddTab(tabs[i].Path, out _);
                if (tab == null)
                {
                    continue;
                }

                if (tabs[i].ScrollTop is double scroll)
                {
                    tab.View = new TabViewState(scroll, 0, Array.Empty<string>());
                }

                if (i == activeIndex)
                {
                    active = tab;
                }
            }

            if (active != null)
            {
                _tabs.Activate(active);
            }

            UpdateTitle();
            CaptureSession();
            if (!SessionState.AreEqual(_settings.Session, _settingsBaseline.Session))
            {
                ScheduleSave();
            }
        }

        private DocumentTab? AddTab(string path, out bool alreadyOpen)
        {
            DocumentTab? tab = _tabs.Open(path, out alreadyOpen);
            if (tab != null && !alreadyOpen)
            {
                AttachWatcher(tab);
            }

            return tab;
        }

        private void AttachWatcher(DocumentTab tab)
        {
            int id = tab.Id;
            var watcher = new DocumentWatcher(tab.Path!, this);
            watcher.Changed += (_, _) => OnTabFileChanged(id);
            tab.Watcher = watcher;
        }

        /// <summary>
        /// Opens each file in its own tab (an already open file activates and reloads its tab);
        /// the last one becomes active. Used by the dialog, drag-drop, links and other instances.
        /// </summary>
        private void OpenFiles(IEnumerable<string> paths, string? fragment = null)
        {
            DocumentTab? last = null;
            foreach (string path in paths)
            {
                DocumentTab? tab = AddTab(path, out bool alreadyOpen);
                if (tab == null)
                {
                    continue;
                }

                if (alreadyOpen)
                {
                    tab.CachedHtml = null; // reload on activation
                    tab.ErrorMessage = null;
                }

                last = tab;
            }

            if (last != null)
            {
                ActivateTab(last, fragment);
            }
            else
            {
                PostTabs();
            }

            SessionChanged();
        }

        // ---------- switching ----------

        private void ActivateTab(DocumentTab? tab, string? fragment = null)
        {
            if (tab == null)
            {
                return;
            }

            _tabs.Activate(tab);
            tab.Updated = false;
            UpdateTitle();
            PostTabs();
            SessionChanged();
            ShowActiveTab(fragment);
        }

        private void ShowActiveTab(string? fragment)
        {
            DocumentTab? tab = _tabs.Active;
            if (tab == null)
            {
                Post(new HostMessage { Type = "welcome" });
                return;
            }

            if (!_pageReady)
            {
                return; // "ready" will show it
            }

            if (tab.IsEditing)
            {
                PostEdit(tab);
            }
            else if (tab.CachedHtml != null)
            {
                PostRender(tab, preserveScroll: false, fragment);
            }
            else
            {
                _ = RenderTabAsync(tab, RenderMode.Show, fragment);
            }
        }

        private void CloseTab(DocumentTab? tab)
        {
            if (tab == null)
            {
                return;
            }

            if (tab.IsEditing)
            {
                RunEditFlow(() => CloseEditingTabAsync(tab));
            }
            else
            {
                RemoveTab(tab);
            }
        }

        private void RemoveTab(DocumentTab tab)
        {
            bool wasActive = ReferenceEquals(tab, _tabs.Active);
            ReleaseTab(tab);
            DocumentTab? next = _tabs.Close(tab);

            UpdateTitle();
            PostTabs();
            SessionChanged();
            if (next == null)
            {
                Post(new HostMessage { Type = "welcome" });
            }
            else if (wasActive)
            {
                next.Updated = false;
                ShowActiveTab(null);
            }
        }

        private void UpdateTitle()
        {
            DocumentTab? tab = _tabs.Active;
            Text = tab == null ? Program.AppName : (tab.IsDirty ? "*" : string.Empty) + tab.DisplayName + " — " + Program.AppName;
        }

        private void PostTabs() => Post(new HostMessage
        {
            Type = "tabs",
            Tabs = _tabs.Tabs.Select(t => new TabInfo
            {
                Id = t.Id,
                Name = t.DisplayName,
                Path = t.Path ?? string.Empty,
                Updated = t.Updated,
                Dirty = t.IsDirty,
                Editing = t.IsEditing,
                Untitled = t.IsUntitled,
            }).ToList(),
            ActiveTabId = _tabs.Active?.Id ?? 0,
        });

        // ---------- session ----------

        private void CaptureSession()
        {
            _settings.Session = SessionState.From(_tabs.Tabs, _tabs.Active);
        }

        /// <summary>Saved (debounced) on every change so a crash doesn't lose the open tabs.</summary>
        private void SessionChanged()
        {
            CaptureSession();
            ScheduleSave();
        }

        // ---------- rendering ----------

        private void ReloadActiveTab()
        {
            if (_tabs.Active != null && !_tabs.Active.IsEditing)
            {
                _ = RenderTabAsync(_tabs.Active, RenderMode.Reload, null);
            }
        }

        private void OnTabFileChanged(int tabId)
        {
            DocumentTab? tab = _tabs.FindById(tabId);
            if (tab == null)
            {
                return;
            }

            if (tab.IsEditing)
            {
                _ = OnEditingFileChangedAsync(tab);
            }
            else if (ReferenceEquals(tab, _tabs.Active))
            {
                _ = RenderTabAsync(tab, RenderMode.AutoReload, null);
            }
            else if (tab.CachedHtml != null)
            {
                _ = RenderTabAsync(tab, RenderMode.Background, null);
            }
        }

        /// <summary>
        /// Reads and renders a tab's file (an untitled tab's text) off the UI thread and updates its
        /// cache. Never throws: failures end in the tab's error state, or are ignored for silent refreshes.
        /// </summary>
        private async Task RenderTabAsync(DocumentTab tab, RenderMode mode, string? fragment)
        {
            if (!_pageReady && mode != RenderMode.Background)
            {
                return;
            }

            int version = ++tab.RenderVersion;
            bool tooLarge = tab.Path is string sizedPath && await Task.Run(() => IsTooLargeToEdit(sizedPath));
            (string? html, string? error) = tab.Path is string path
                ? await ReadAndRenderAsync(path)
                : await RenderTextAsync(tab.Document?.Body ?? string.Empty, null);

            if (version != tab.RenderVersion || !_tabs.Tabs.Contains(tab) || IsDisposed)
            {
                return;
            }

            bool isActive = ReferenceEquals(tab, _tabs.Active);
            if (error != null || html == null)
            {
                // Silent refreshes (file mid-save or deleted) keep the last good content.
                if (mode == RenderMode.Background || (mode == RenderMode.AutoReload && tab.CachedHtml != null))
                {
                    return;
                }

                tab.CachedHtml = null;
                tab.ErrorMessage = error ?? "The document could not be displayed.";
                if (isActive)
                {
                    Post(new HostMessage
                    {
                        Type = "error",
                        TabId = tab.Id,
                        Message = tab.ErrorMessage,
                        FileName = tab.DisplayName,
                        FilePath = tab.Path,
                    });
                }

                return;
            }

            bool changed = !string.Equals(tab.CachedHtml, html, StringComparison.Ordinal);
            tab.CachedHtml = html;
            tab.ErrorMessage = null;
            tab.TooLargeToEdit = tooLarge;

            if (!isActive)
            {
                if (mode == RenderMode.Background && changed)
                {
                    tab.Updated = true;
                    PostTabs();
                }

                return;
            }

            PostRender(tab, preserveScroll: mode != RenderMode.Show, fragment);
        }

        private void PostRender(DocumentTab tab, bool preserveScroll, string? fragment)
        {
            if (tab.CachedHtml == null)
            {
                if (tab.ErrorMessage != null)
                {
                    Post(new HostMessage { Type = "error", TabId = tab.Id, Message = tab.ErrorMessage, FileName = tab.DisplayName, FilePath = tab.Path });
                }

                return;
            }

            TabViewState? view = tab.View;
            bool restore = !preserveScroll && fragment == null && view != null;
            Post(new HostMessage
            {
                Type = "render",
                TabId = tab.Id,
                Html = tab.CachedHtml,
                FileName = tab.DisplayName,
                FilePath = tab.Path,
                ImageBase = ImageBaseFor(tab),
                PreserveScroll = preserveScroll,
                Fragment = fragment,
                RestoreView = restore,
                TooLarge = tab.TooLargeToEdit,
                ScrollTop = restore ? view!.ScrollTop : 0,
                OutlineScrollTop = restore ? view!.OutlineScrollTop : 0,
                Collapsed = restore ? view!.CollapsedKeys : null,
            });
        }

        private static bool IsTooLargeToEdit(string path)
        {
            try
            {
                return new FileInfo(path).Length > MaxEditableBytes;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }

        private static string? ImageBaseFor(DocumentTab tab) =>
            tab.Path == null ? null : LocalImageUrls.DirectoryBaseUrl(Path.GetDirectoryName(tab.Path) ?? tab.Path);

        private async Task<(string? Html, string? Error)> ReadAndRenderAsync(string path)
        {
            (string? text, _, string? error) = await ReadDocumentAsync(path);
            return text == null ? (null, error) : await RenderTextAsync(text, path);
        }

        private async Task<(string? Html, string? Error)> RenderTextAsync(string text, string? path)
        {
            try
            {
                return (await Task.Run(() => _renderer.RenderHtml(text, path)), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, "The document could not be displayed: " + ex.Message);
            }
        }

        /// <summary>Reads a file off the UI thread for viewing or editing. Never throws: failures come back as a message for the user.</summary>
        private static async Task<(string? Text, Encoding? Encoding, string? Error)> ReadDocumentAsync(string path, long maxBytes = TextFileReader.MaxFileBytes)
        {
            try
            {
                return await Task.Run<(string?, Encoding?, string?)>(() =>
                {
                    if (new FileInfo(path).Length > maxBytes)
                    {
                        throw new InvalidDataException($"The file is larger than {maxBytes / (1024 * 1024)} MB.");
                    }

                    string text = ReadWithRetry(path, out Encoding encoding);
                    return (text, encoding, null);
                });
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return (null, null, "The file was not found. It may have been moved, renamed or deleted.");
            }
            catch (UnauthorizedAccessException)
            {
                return (null, null, "Access to the file was denied.");
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                return (null, null, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                return (null, null, "The file path is not valid.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, null, "The document could not be displayed: " + ex.Message);
            }
        }

        /// <summary>Editors briefly lock files while saving; retry a few times before giving up.</summary>
        private static string ReadWithRetry(string path, out Encoding encoding)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return TextFileReader.ReadAllText(path, out encoding);
                }
                catch (IOException ex) when (attempt < 3 && ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }
}
