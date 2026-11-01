using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                int id = tab.Id;
                var watcher = new DocumentWatcher(tab.Path, this);
                watcher.Changed += (_, _) => OnTabFileChanged(id);
                tab.Watcher = watcher;
            }

            return tab;
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

            if (tab.CachedHtml != null)
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

            bool wasActive = ReferenceEquals(tab, _tabs.Active);
            tab.Watcher?.Dispose();
            tab.Watcher = null;
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
            Text = _tabs.Active == null ? Program.AppName : _tabs.Active.FileName + " — " + Program.AppName;
        }

        private void PostTabs() => Post(new HostMessage
        {
            Type = "tabs",
            Tabs = _tabs.Tabs.Select(t => new TabInfo { Id = t.Id, Name = t.FileName, Path = t.Path, Updated = t.Updated }).ToList(),
            ActiveTabId = _tabs.Active?.Id ?? 0,
        });

        // ---------- session ----------

        private void CaptureSession()
        {
            _settings.Session = new SessionState
            {
                Tabs = _tabs.Tabs.Select(t => new SessionTab { Path = t.Path, ScrollTop = Math.Round(t.View?.ScrollTop ?? 0) }).ToList(),
                ActiveIndex = _tabs.ActiveIndex,
            };
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
            if (_tabs.Active != null)
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

            if (ReferenceEquals(tab, _tabs.Active))
            {
                _ = RenderTabAsync(tab, RenderMode.AutoReload, null);
            }
            else if (tab.CachedHtml != null)
            {
                _ = RenderTabAsync(tab, RenderMode.Background, null);
            }
        }

        /// <summary>
        /// Reads and renders a tab's file off the UI thread and updates its cache. Never throws:
        /// failures end in the tab's error state, or are ignored for silent refreshes.
        /// </summary>
        private async Task RenderTabAsync(DocumentTab tab, RenderMode mode, string? fragment)
        {
            if (!_pageReady && mode != RenderMode.Background)
            {
                return;
            }

            int version = ++tab.RenderVersion;
            string path = tab.Path;
            (string? html, string? error) = await ReadAndRenderAsync(path);

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
                        FileName = tab.FileName,
                        FilePath = tab.Path,
                    });
                }

                return;
            }

            bool changed = !string.Equals(tab.CachedHtml, html, StringComparison.Ordinal);
            tab.CachedHtml = html;
            tab.ErrorMessage = null;

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
                    Post(new HostMessage { Type = "error", TabId = tab.Id, Message = tab.ErrorMessage, FileName = tab.FileName, FilePath = tab.Path });
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
                FileName = tab.FileName,
                FilePath = tab.Path,
                ImageBase = LocalImageUrls.DirectoryBaseUrl(Path.GetDirectoryName(tab.Path) ?? tab.Path),
                PreserveScroll = preserveScroll,
                Fragment = fragment,
                RestoreView = restore,
                ScrollTop = restore ? view!.ScrollTop : 0,
                OutlineScrollTop = restore ? view!.OutlineScrollTop : 0,
                Collapsed = restore ? view!.CollapsedKeys : null,
            });
        }

        private async Task<(string? Html, string? Error)> ReadAndRenderAsync(string path)
        {
            try
            {
                string text;
                try
                {
                    text = await Task.Run(() => ReadWithRetry(path));
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    return (null, "The file was not found. It may have been moved, renamed or deleted.");
                }
                catch (UnauthorizedAccessException)
                {
                    return (null, "Access to the file was denied.");
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
                {
                    return (null, ex.Message);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
                {
                    return (null, "The file path is not valid.");
                }

                return (await Task.Run(() => _renderer.RenderHtml(text, path)), null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, "The document could not be displayed: " + ex.Message);
            }
        }

        /// <summary>Editors briefly lock files while saving; retry a few times before giving up.</summary>
        private static string ReadWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return TextFileReader.ReadAllText(path);
                }
                catch (IOException ex) when (attempt < 3 && ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }
}
