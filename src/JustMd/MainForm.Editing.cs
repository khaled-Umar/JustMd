using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using JustMd.Core;
using JustMd.Core.Editing;
using JustMd.Core.Messaging;
using JustMd.Core.Tabs;

namespace JustMd
{
    /// <summary>Edit mode: entering and leaving it, saving, prompts and files changing underneath the editor.</summary>
    internal sealed partial class MainForm
    {
        /// <summary>The one exception to "a document only enters edit mode when the user asks".</summary>
        private const bool NewFileEntersEditMode = true;

        private const long MaxEditableBytes = 4L * 1024 * 1024;
        private const int ContentTimeoutMilliseconds = 5000;

        private readonly Dictionary<int, TaskCompletionSource<WebMessage?>> _contentRequests = new();

        /// <summary>Tab id to the revision last sent to the page. A tab missing here, or behind, has no current editor state.</summary>
        private readonly Dictionary<int, int> _pageRevisions = new();

        /// <summary>Tabs being written right now: the watcher event our own write causes must wait for the outcome.</summary>
        private readonly HashSet<int> _savesInFlight = new();
        private readonly HashSet<int> _changedDuringSave = new();

        /// <summary>Background tabs whose "changed on disk" banner is shown when they are next activated.</summary>
        private readonly HashSet<int> _externalChangeNotShown = new();

        private int _nextRequestId;
        private bool _editFlowBusy;
        private bool _closeApproved;

        private enum UnsavedChanges
        {
            None,
            Saved,
            Discarded,
            Cancelled,
        }

        // ---------- keyboard ----------

        /// <summary>Consulted first while the active tab is editing. Null means the normal shortcut table applies.</summary>
        private Action? GetEditorShortcutAction(Keys keyData)
        {
            if ((keyData & Keys.Modifiers) == (Keys.Control | Keys.Shift))
            {
                Keys key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D6)
                {
                    return () => PostEditorCommand("setHeading", (key - Keys.D0).ToString(CultureInfo.InvariantCulture));
                }
            }

            switch (keyData)
            {
                case Keys.Control | Keys.Z:
                    return () => PostEditorCommand("undo");

                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z:
                    return () => PostEditorCommand("redo");

                case Keys.Control | Keys.B:
                    return () => PostEditorCommand("toggleBold");

                case Keys.Control | Keys.I:
                    return () => PostEditorCommand("toggleItalic");

                case Keys.Control | Keys.Shift | Keys.X:
                    return () => PostEditorCommand("toggleStrike");

                case Keys.Control | Keys.E:
                    return () => PostEditorCommand("toggleCode");

                // Windows can reserve Ctrl+Shift+0 for switching keyboard layouts, so it never arrives there.
                case Keys.Control | Keys.Shift | Keys.P:
                case Keys.Control | Keys.Shift | Keys.D0:
                    return () => PostEditorCommand("setParagraph");

                case Keys.Control | Keys.Shift | Keys.D7:
                    return () => PostEditorCommand("toggleOrderedList");

                case Keys.Control | Keys.Shift | Keys.D8:
                    return () => PostEditorCommand("toggleBulletList");

                case Keys.Control | Keys.Shift | Keys.D9:
                    return () => PostEditorCommand("toggleTaskList");

                case Keys.Control | Keys.Shift | Keys.Q:
                    return () => PostEditorCommand("toggleBlockquote");

                case Keys.Control | Keys.Shift | Keys.C:
                    return () => PostEditorCommand("setCodeBlock");

                case Keys.Control | Keys.Shift | Keys.H:
                    return () => PostEditorCommand("insertHorizontalRule");

                case Keys.Control | Keys.Shift | Keys.T:
                    return () => PostEditorCommand("insertTable");

                case Keys.Control | Keys.K:
                    return () => PostEditorCommand("setLink");

                case Keys.Control | Keys.Shift | Keys.I:
                    return () => PostEditorCommand("insertImage");

                case Keys.Control | Keys.Shift | Keys.D:
                    return () => PostEditorCommand("toggleRtl");

                // Reloading would throw the editor away, and the outline is hidden while editing.
                case Keys.F5:
                case Keys.Control | Keys.R:
                case Keys.Shift | Keys.F5:
                case Keys.Control | Keys.F5:
                case Keys.Control | Keys.Shift | Keys.R:
                case Keys.BrowserRefresh:
                case Keys.Control | Keys.Shift | Keys.O:
                    return static () => { };

                default:
                    return null;
            }
        }

        private void PostEditorCommand(string command, string? arg = null)
        {
            if (_tabs.Active is { IsEditing: true } tab)
            {
                Post(new HostMessage { Type = "editorCommand", TabId = tab.Id, Command = command, Arg = arg });
            }
        }

        // ---------- commands ----------

        /// <summary>
        /// Runs a flow that may prompt or wait for the page. A second one started meanwhile (a
        /// queued key press, say, which runs inside the prompt's message loop) is dropped.
        /// </summary>
        private async void RunEditFlow(Func<Task> flow)
        {
            if (_editFlowBusy || IsDisposed)
            {
                return;
            }

            _editFlowBusy = true;
            try
            {
                await flow();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing was discarded: the edits are still in the editor.
                ShowEditError("That did not work. Your changes are still in the editor.\n\n" + ex.Message);
            }
            finally
            {
                _editFlowBusy = false;
            }
        }

        private void NewFile()
        {
            DocumentTab tab = _tabs.NewUntitled();
            tab.Document = DocumentText.CreateEmpty();
            if (NewFileEntersEditMode)
            {
                BeginEditing(tab, tab.Document, lastSavedText: null);
            }

            ActivateTab(tab);
        }

        private void ToggleEditActiveTab()
        {
            if (_tabs.Active is not DocumentTab tab)
            {
                return;
            }

            RunEditFlow(() => tab.IsEditing ? ExitEditAsync(tab) : EnterEditAsync(tab));
        }

        private void SaveTab(DocumentTab? tab)
        {
            if (tab is { IsEditing: true })
            {
                RunEditFlow(() => SaveAsync(tab));
            }
        }

        private void SaveTabAs(DocumentTab? tab)
        {
            if (tab is { IsEditing: true })
            {
                RunEditFlow(() => SaveAsAsync(tab));
            }
        }

        /// <summary>The folder of the active tab, or else of the titled tab closest to it. Null if there is none.</summary>
        private string? NearestDocumentFolder()
        {
            IReadOnlyList<DocumentTab> tabs = _tabs.Tabs;
            int start = Math.Max(0, _tabs.ActiveIndex);
            for (int distance = 0; distance < tabs.Count; distance++)
            {
                foreach (int index in new[] { start - distance, start + distance })
                {
                    if (index >= 0 && index < tabs.Count && tabs[index].Path is string path)
                    {
                        string? folder = Path.GetDirectoryName(path);
                        if (folder != null && Directory.Exists(folder))
                        {
                            return folder;
                        }
                    }
                }
            }

            return null;
        }

        // ---------- entering and leaving ----------

        private async Task EnterEditAsync(DocumentTab tab)
        {
            if (tab.IsEditing)
            {
                return;
            }

            if (tab.Path is string path)
            {
                (string? text, Encoding? encoding, string? error) = await ReadDocumentAsync(path, MaxEditableBytes);
                if (tab.IsEditing || !_tabs.Tabs.Contains(tab))
                {
                    return;
                }

                if (text == null || encoding == null)
                {
                    ShowEditError(tab.DisplayName + " cannot be edited.\n\n" + error);
                    return;
                }

                BeginEditing(tab, DocumentText.Parse(text, encoding), text);
            }
            else
            {
                BeginEditing(tab, tab.Document ?? DocumentText.CreateEmpty(), lastSavedText: null);
            }

            UpdateTitle();
            PostTabs();
            if (ReferenceEquals(tab, _tabs.Active))
            {
                ShowActiveTab(null);
            }
        }

        private void BeginEditing(DocumentTab tab, DocumentText document, string? lastSavedText)
        {
            tab.RenderVersion++; // a render still on its way must not replace the editor
            tab.IsEditing = true;
            SetEditorDocument(tab, document, lastSavedText);
        }

        private static void SetEditorDocument(DocumentTab tab, DocumentText document, string? lastSavedText)
        {
            tab.Document = document;
            tab.LastSavedText = lastSavedText;
            tab.EditRevision++;
            tab.IsDirty = false;
            tab.ExternalChangePending = false;
        }

        private void PostEdit(DocumentTab tab)
        {
            if (tab.Document == null)
            {
                return;
            }

            bool spelling = _spelling.IsAvailable;
            _pageRevisions[tab.Id] = tab.EditRevision;
            Post(new HostMessage
            {
                Type = "edit",
                TabId = tab.Id,
                Revision = tab.EditRevision,
                Markdown = tab.Document.Body,
                Rtl = tab.Document.IsRtl,
                FileName = tab.DisplayName,
                FilePath = tab.Path,
                ImageBase = ImageBaseFor(tab),
                Untitled = tab.IsUntitled,
                Spelling = spelling,
                NativeSpelling = !spelling || !_spelling.Checks(CultureInfo.CurrentUICulture.Name),
            });

            if (_externalChangeNotShown.Remove(tab.Id))
            {
                Post(new HostMessage { Type = "externalChange", TabId = tab.Id });
            }
        }

        private async Task ExitEditAsync(DocumentTab tab)
        {
            if (!tab.IsEditing)
            {
                return;
            }

            UnsavedChanges outcome = await ResolveUnsavedChangesAsync(tab);
            if (outcome == UnsavedChanges.Cancelled)
            {
                return;
            }

            if (outcome == UnsavedChanges.Discarded && tab.IsUntitled)
            {
                RemoveTab(tab);
                return;
            }

            tab.IsEditing = false;
            tab.IsDirty = false;
            tab.ExternalChangePending = false;
            tab.CachedHtml = null;
            tab.LastSavedText = null;
            if (!tab.IsUntitled)
            {
                tab.Document = null; // an untitled tab has nothing else to show
            }

            ForgetPageEditor(tab);
            UpdateTitle();
            PostTabs();
            if (ReferenceEquals(tab, _tabs.Active))
            {
                _ = RenderTabAsync(tab, RenderMode.Show, null);
            }
        }

        private async Task CloseEditingTabAsync(DocumentTab tab)
        {
            if (await ResolveUnsavedChangesAsync(tab) != UnsavedChanges.Cancelled)
            {
                RemoveTab(tab);
            }
        }

        /// <summary>Asks about unsaved changes, if there are any, and saves them if the user wants that.</summary>
        private async Task<UnsavedChanges> ResolveUnsavedChangesAsync(DocumentTab tab)
        {
            await RefreshDirtyAsync(tab);
            if (!tab.IsEditing || !_tabs.Tabs.Contains(tab))
            {
                return UnsavedChanges.Cancelled;
            }

            if (!tab.IsDirty)
            {
                return UnsavedChanges.None;
            }

            DialogResult choice = MessageBox.Show(this, "Save changes to " + tab.DisplayName + "?",
                Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return choice switch
            {
                DialogResult.Yes => await SaveAsync(tab) ? UnsavedChanges.Saved : UnsavedChanges.Cancelled,
                DialogResult.No => UnsavedChanges.Discarded,
                _ => UnsavedChanges.Cancelled,
            };
        }

        /// <summary>
        /// The page reports changes up to 250 ms late, so before anything is thrown away ask it for
        /// the text itself: a key pressed just before closing must still count. If the page cannot
        /// answer, the last report stands.
        /// </summary>
        private async Task RefreshDirtyAsync(DocumentTab tab)
        {
            if (!tab.IsEditing || tab.IsDirty || tab.Document == null
                || !_pageRevisions.TryGetValue(tab.Id, out int revision) || revision != tab.EditRevision)
            {
                return;
            }

            WebMessage? content = await RequestContentAsync(tab);
            if (content != null && tab.IsEditing && tab.Document != null && revision == tab.EditRevision
                && (content.Error != null || tab.Document.With(content.Markdown!, content.Rtl).Compose() != tab.Document.Compose()))
            {
                SetDirty(tab, true);
            }
        }

        private void SetDirty(DocumentTab tab, bool dirty)
        {
            if (tab.IsDirty != dirty)
            {
                tab.IsDirty = dirty;
                UpdateTitle();
                PostTabs();
            }
        }

        /// <summary>Stops watching a tab that is going away and lets the page drop its editor state.</summary>
        private void ReleaseTab(DocumentTab tab)
        {
            tab.Watcher?.Dispose();
            tab.Watcher = null;
            if (tab.IsEditing)
            {
                tab.IsEditing = false;
                tab.IsDirty = false;
                ForgetPageEditor(tab);
            }
        }

        private void ForgetPageEditor(DocumentTab tab)
        {
            _pageRevisions.Remove(tab.Id);
            _externalChangeNotShown.Remove(tab.Id);
            _spelling.Forget(tab.Id);
            Post(new HostMessage { Type = "exitEdit", TabId = tab.Id });
        }

        // ---------- closing the window ----------

        private async Task ConfirmAppCloseAsync()
        {
            foreach (DocumentTab tab in _tabs.Tabs.Where(t => t.IsEditing).ToList())
            {
                await RefreshDirtyAsync(tab);
            }

            List<DocumentTab> dirty = _tabs.Tabs.Where(t => t.IsEditing && t.IsDirty).ToList();
            if (dirty.Count > 0)
            {
                DialogResult choice = MessageBox.Show(this,
                    "Save changes to these documents before closing?\n\n" + string.Join("\n", dirty.Select(t => t.DisplayName)),
                    Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.Cancel)
                {
                    return;
                }

                if (choice == DialogResult.Yes)
                {
                    foreach (DocumentTab tab in dirty)
                    {
                        if (!_tabs.Tabs.Contains(tab))
                        {
                            continue; // replaced by an earlier Save As
                        }

                        if (tab.IsUntitled)
                        {
                            ActivateTab(tab); // show what the Save As dialog is about
                        }

                        if (!await SaveAsync(tab))
                        {
                            return;
                        }
                    }
                }
            }

            _closeApproved = true;
            Close();
        }

        // ---------- saving ----------

        private async Task<bool> SaveAsync(DocumentTab tab)
        {
            if (!tab.IsEditing)
            {
                return false;
            }

            if (tab.Path is not string path)
            {
                return await SaveAsAsync(tab);
            }

            bool changedOnDisk = await ChangedOnDiskAsync(tab, path);
            if (!IsStillEditing(tab, path))
            {
                return false;
            }

            if (changedOnDisk)
            {
                DialogResult choice = MessageBox.Show(this,
                    tab.DisplayName + " changed on disk after you started editing.\n\nOverwrite the newer file on disk?\n\n" +
                    "Yes overwrites it. No saves your version under another name. Cancel saves nothing.",
                    Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (choice == DialogResult.No)
                {
                    return await SaveAsAsync(tab);
                }

                if (choice != DialogResult.Yes)
                {
                    return false;
                }
            }

            return await WriteFromEditorAsync(tab, path);
        }

        /// <summary>
        /// The watcher reports with a delay and can miss events, so look at the file itself. The
        /// text went through the same decoder as LastSavedText, and a save only happens if its text
        /// survives that decoder, so an unchanged file compares equal. A file that is gone or
        /// cannot be read has nothing newer to lose.
        /// </summary>
        private async Task<bool> ChangedOnDiskAsync(DocumentTab tab, string path)
        {
            if (tab.ExternalChangePending)
            {
                return true;
            }

            // A file that exists but cannot be read (locked, grown past the limit) may be newer: ask.
            (string? text, _, _) = await ReadDocumentAsync(path);
            return text == null ? File.Exists(path) : !string.Equals(text, tab.LastSavedText, StringComparison.Ordinal);
        }

        private async Task<bool> SaveAsAsync(DocumentTab tab)
        {
            if (!tab.IsEditing || ShowSaveDialog(tab) is not string picked)
            {
                return false;
            }

            string? target = DocumentPaths.Normalize(picked);
            if (target == null)
            {
                ShowEditError("That file name is not valid. Your changes are still in the editor.");
                return false;
            }

            // Taking over a path closes the tab showing it, which must not silently cost its edits.
            DocumentTab? other = _tabs.Find(target);
            if (other != null && !ReferenceEquals(other, tab))
            {
                await RefreshDirtyAsync(other);
                if (other.IsDirty && MessageBox.Show(this,
                        other.DisplayName + " is open in another tab with unsaved changes. Saving here closes that tab and discards them.\n\nContinue?",
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return false;
                }
            }

            return await WriteFromEditorAsync(tab, target);
        }

        private string? ShowSaveDialog(DocumentTab tab)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save " + tab.DisplayName + " as",
                Filter = MarkdownFiles.DialogFilter,
                DefaultExt = "md",
                AddExtension = true,
                OverwritePrompt = true,
                RestoreDirectory = true,
                FileName = tab.IsUntitled ? tab.DisplayName + ".md" : tab.FileName,
            };

            string? folder = NearestDocumentFolder();
            if (folder != null)
            {
                dialog.InitialDirectory = folder;
            }

            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        /// <summary>
        /// Gets the text from the page and writes it to <paramref name="path"/>. On any failure the
        /// user is told, nothing about the tab changes and the edits stay in the editor.
        /// </summary>
        private async Task<bool> WriteFromEditorAsync(DocumentTab tab, string path)
        {
            WebMessage? content = await RequestContentAsync(tab);
            if (content?.Error == WebMessage.ErrorTooLarge && tab.IsEditing)
            {
                ShowEditError(tab.DisplayName + " is too large to save: the editor holds more than 8 million characters. " +
                              "Remove some content and try again. Your changes are still in the editor.");
                return false;
            }

            if (content == null || content.Error != null || !tab.IsEditing || tab.Document == null)
            {
                ShowEditError("The editor did not hand over the document, so " + tab.DisplayName +
                              " was not saved. Your changes are still in the editor.");
                return false;
            }

            DocumentText document = tab.Document.With(content.Markdown!, content.Rtl);
            if (!document.SurvivesEncoding())
            {
                DocumentText utf8 = document.AsUtf8();
                if (!utf8.SurvivesEncoding())
                {
                    ShowEditError(tab.DisplayName + " contains text that no encoding can store (a broken Unicode character), " +
                                  "so it was not saved. Your changes are still in the editor.");
                    return false;
                }

                if (!ConfirmSaveAsUtf8(tab, document.Encoding))
                {
                    return false;
                }

                document = utf8;
            }

            _savesInFlight.Add(tab.Id);
            try
            {
                byte[] bytes = document.ToBytes();
                await Task.Run(() => TextFileWriter.Write(path, bytes));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                // ArgumentException includes EncoderFallbackException from a strict encoding.
                _changedDuringSave.Remove(tab.Id);
                ShowEditError(tab.DisplayName + " was not saved. Your changes are still in the editor.\n\n" + ex.Message);
                return false;
            }
            finally
            {
                _savesInFlight.Remove(tab.Id);
            }

            if (!string.Equals(tab.Path, path, StringComparison.Ordinal))
            {
                ApplyNewPath(tab, path);
            }

            tab.Document = document;
            tab.LastSavedText = document.Compose();
            tab.CachedHtml = null;
            tab.ErrorMessage = null;
            tab.IsDirty = false;
            tab.ExternalChangePending = false;
            _externalChangeNotShown.Remove(tab.Id);
            Post(new HostMessage { Type = "saved", TabId = tab.Id, Revision = tab.EditRevision });
            UpdateTitle();
            PostTabs();

            if (_changedDuringSave.Remove(tab.Id))
            {
                _ = OnEditingFileChangedAsync(tab);
            }

            return true;
        }

        /// <summary>
        /// A plain message box cannot offer Yes and Cancel without No. Defaults to Cancel: changing
        /// a file's encoding is not something to do on a stray Enter.
        /// </summary>
        private bool ConfirmSaveAsUtf8(DocumentTab tab, Encoding current)
        {
            var page = new TaskDialogPage
            {
                Caption = Program.AppName,
                Heading = "Save " + tab.DisplayName + " as UTF-8?",
                Text = "It uses the " + current.WebName + " encoding, which cannot store some of the characters in this document. " +
                       "UTF-8 keeps every character, and the document stays UTF-8 from now on.\n\n" +
                       "Cancel saves nothing. Your changes stay in the editor.",
                Icon = TaskDialogIcon.Warning,
                AllowCancel = true,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
            };

            return TaskDialog.ShowDialog(this, page) == TaskDialogButton.Yes;
        }

        private void ApplyNewPath(DocumentTab tab, string path)
        {
            DocumentTab? activeBefore = _tabs.Active;
            if (!_tabs.SetPath(tab, path, out DocumentTab? displaced))
            {
                return;
            }

            if (displaced != null)
            {
                ReleaseTab(displaced);
            }

            tab.Watcher?.Dispose();
            AttachWatcher(tab);
            Post(new HostMessage
            {
                Type = "documentRenamed",
                TabId = tab.Id,
                Revision = tab.EditRevision,
                FileName = tab.DisplayName,
                FilePath = tab.Path,
                ImageBase = ImageBaseFor(tab),
            });
            SessionChanged();

            // The tab that went away may have been the one on screen (saving all while closing).
            if (!ReferenceEquals(activeBefore, _tabs.Active))
            {
                ShowActiveTab(null);
            }
        }

        private async Task<WebMessage?> RequestContentAsync(DocumentTab tab)
        {
            if (!_pageReady)
            {
                return null;
            }

            int requestId = ++_nextRequestId;
            var request = new TaskCompletionSource<WebMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _contentRequests[requestId] = request;
            Post(new HostMessage { Type = "requestContent", TabId = tab.Id, RequestId = requestId });

            Task finished = await Task.WhenAny(request.Task, Task.Delay(ContentTimeoutMilliseconds));
            _contentRequests.Remove(requestId);
            WebMessage? content = finished == request.Task ? await request.Task : null;
            return content?.TabId == tab.Id ? content : null;
        }

        private void CompleteContentRequest(WebMessage message)
        {
            if (_contentRequests.TryGetValue(message.RequestId, out TaskCompletionSource<WebMessage?>? request))
            {
                request.TrySetResult(message);
            }
        }

        // ---------- files changing on disk ----------

        private async Task OnEditingFileChangedAsync(DocumentTab tab)
        {
            if (_savesInFlight.Contains(tab.Id))
            {
                _changedDuringSave.Add(tab.Id); // looked at when the save has finished
                return;
            }

            if (tab.Path is not string path)
            {
                return;
            }

            (string? text, Encoding? encoding, _) = await ReadDocumentAsync(path);
            if (text == null || encoding == null || !IsStillEditing(tab, path))
            {
                return; // mid-save by another program, or deleted: nothing to compare yet
            }

            if (string.Equals(text, tab.LastSavedText, StringComparison.Ordinal))
            {
                return; // our own save
            }

            await RefreshDirtyAsync(tab);
            if (!IsStillEditing(tab, path))
            {
                return;
            }

            bool isActive = ReferenceEquals(tab, _tabs.Active);
            if (tab.IsDirty)
            {
                tab.ExternalChangePending = true;
                if (isActive)
                {
                    Post(new HostMessage { Type = "externalChange", TabId = tab.Id });
                }
                else
                {
                    _externalChangeNotShown.Add(tab.Id);
                    tab.Updated = true;
                    PostTabs();
                }
            }
            else
            {
                ApplyDiskText(tab, text, encoding);
            }
        }

        private bool IsStillEditing(DocumentTab tab, string path) =>
            tab.IsEditing && _tabs.Tabs.Contains(tab) && string.Equals(tab.Path, path, StringComparison.Ordinal);

        private async Task ReloadFromDiskAsync(DocumentTab tab)
        {
            if (!tab.IsEditing || tab.Path is not string path)
            {
                return;
            }

            (string? text, Encoding? encoding, string? error) = await ReadDocumentAsync(path);
            if (!IsStillEditing(tab, path))
            {
                return;
            }

            if (text == null || encoding == null)
            {
                ShowEditError(tab.DisplayName + " could not be reloaded. Your changes are still in the editor.\n\n" + error);
                return;
            }

            ApplyDiskText(tab, text, encoding);
        }

        /// <summary>Replaces the editor's document with the file's text under a new revision.</summary>
        private void ApplyDiskText(DocumentTab tab, string text, Encoding encoding)
        {
            SetEditorDocument(tab, DocumentText.Parse(text, encoding), text);
            _spelling.Forget(tab.Id); // the queued scan is of the text just replaced
            _externalChangeNotShown.Remove(tab.Id);
            if (ReferenceEquals(tab, _tabs.Active))
            {
                PostEdit(tab);
            }
            else
            {
                tab.Updated = true;
            }

            UpdateTitle();
            PostTabs();
        }

        // ---------- images ----------

        private void ShowImageDialog(DocumentTab tab)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Insert image",
                Filter = LocalImageUrls.DialogFilter,
                CheckFileExists = true,
                RestoreDirectory = true,
            };

            string? folder = NearestDocumentFolder();
            if (folder != null)
            {
                dialog.InitialDirectory = folder;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK && tab.IsEditing)
            {
                PostImagePicked(tab, dialog.FileName);
            }
        }

        private void PostImagePicked(DocumentTab tab, string imagePath)
        {
            if (LocalImageUrls.IsImageExtension(imagePath))
            {
                Post(new HostMessage
                {
                    Type = "imagePicked",
                    TabId = tab.Id,
                    Src = RelativeReferences.ForImage(tab.Path, imagePath),
                    Alt = Path.GetFileNameWithoutExtension(imagePath),
                });
            }
        }

        // ---------- page restarts ----------

        /// <summary>
        /// A newly loaded page has no editor states: tabs rebuild from their document when shown,
        /// and pending requests will never be answered. Returns true if unsaved changes went with it.
        /// </summary>
        private bool ResetEditorsForNewPage()
        {
            foreach (TaskCompletionSource<WebMessage?> request in _contentRequests.Values)
            {
                request.TrySetResult(null);
            }

            _pageRevisions.Clear();
            bool editsLost = false;
            foreach (DocumentTab tab in _tabs.Tabs.Where(t => t.IsEditing))
            {
                editsLost |= tab.IsDirty;
                tab.IsDirty = false;
                _spelling.Forget(tab.Id);
                if (tab.ExternalChangePending)
                {
                    _externalChangeNotShown.Add(tab.Id);
                }
            }

            if (editsLost)
            {
                UpdateTitle();
            }

            return editsLost;
        }

        private void ShowEditError(string message)
        {
            if (!IsDisposed)
            {
                MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
