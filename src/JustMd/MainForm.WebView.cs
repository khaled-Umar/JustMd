using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using JustMd.Core;
using JustMd.Core.Messaging;
using JustMd.Core.Settings;
using JustMd.Core.Tabs;
using Microsoft.Web.WebView2.Core;

namespace JustMd
{
    /// <summary>WebView2 setup, page messaging, navigation guards and local-image serving.</summary>
    internal sealed partial class MainForm
    {
        private const long MaxImageBytes = 64L * 1024 * 1024;

        private bool _pageReady;

        private CoreWebView2? Core => _webView.CoreWebView2;

        private async Task InitializeWebViewAsync()
        {
            string webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!File.Exists(Path.Combine(webRoot, "viewer.html")))
            {
                MessageBox.Show(this, "The viewer files are missing from the installation folder:\n\n" + webRoot +
                                      "\n\nPlease reinstall JustMd.",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.WebViewDataDirectory);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppPaths.WebViewDataDirectory,
                    options: new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
                await _webView.EnsureCoreWebView2Async(environment);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (!IsDisposed)
                {
                    Program.ShowWebViewRuntimeMissing(this);
                    Close();
                }

                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!IsDisposed && !Disposing)
                {
                    MessageBox.Show(this, "The viewer component (Microsoft Edge WebView2) could not be started.\n\n" + ex.Message,
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                }

                return;
            }

            if (IsDisposed || Core == null)
            {
                return;
            }

            ConfigureWebView(Core, webRoot);
            _webView.ZoomFactor = _settings.Zoom;
            _webView.ZoomFactorChanged += (_, _) =>
            {
                _settings.Zoom = AppSettings.ClampZoom(_webView.ZoomFactor);
                ScheduleSave();
            };

            Core.Navigate(VirtualHosts.ViewerUrl + "?theme=" + _palette.CssName);
        }

        private void ConfigureWebView(CoreWebView2 core, string webRoot)
        {
            CoreWebView2Settings settings = core.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreDefaultContextMenusEnabled = true; // trimmed to Copy / Select all below
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = true;
            settings.IsScriptEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsZoomControlEnabled = true;
            settings.IsBuiltInErrorPageEnabled = true;
            settings.IsReputationCheckingRequired = false; // only local content is ever loaded

            // Kept on so Ctrl+F (find), Ctrl+C and Ctrl+A work; the shortcuts that make no sense
            // here (reload, history, devtools, save, ...) are swallowed in ProcessCmdKey.
            settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SetVirtualHostNameToFolderMapping(VirtualHosts.App, webRoot, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("https://" + VirtualHosts.Images + "/*", CoreWebView2WebResourceContext.All);

            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.WebResourceRequested += OnWebResourceRequested;
            core.ContextMenuRequested += OnContextMenuRequested;
            core.DownloadStarting += (_, e) =>
            {
                e.Cancel = true;
                e.Handled = true;
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += OnProcessFailed;

            ApplyWebViewColorScheme();
        }

        private void ApplyWebViewColorScheme()
        {
            try
            {
                if (Core != null)
                {
                    Core.Profile.PreferredColorScheme = _palette.IsDark
                        ? CoreWebView2PreferredColorScheme.Dark
                        : CoreWebView2PreferredColorScheme.Light;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is COMException || ex is NotImplementedException)
            {
                // Older runtime: the page's own color-scheme CSS still applies.
            }
        }

        private static bool IsViewerUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, VirtualHosts.App, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && uri.AbsolutePath == "/viewer.html";

        // ---------- navigation guards ----------

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsViewerUrl(e.Uri))
            {
                return;
            }

            // Defence in depth: the page never navigates. If a click somehow gets past the page
            // script, route it through the same checks as a normal link click.
            e.Cancel = true;
            if (e.IsUserInitiated)
            {
                string uri = e.Uri;
                BeginInvoke(() => HandleNavigationAttempt(uri));
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (e.IsUserInitiated)
            {
                string uri = e.Uri;
                BeginInvoke(() => HandleNavigationAttempt(uri));
            }
        }

        private void HandleNavigationAttempt(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            if (uri.IsFile)
            {
                // A file dropped where the page did not catch it.
                string local = uri.LocalPath;
                if (!uri.IsUnc && MarkdownFiles.IsMarkdownPath(local) && File.Exists(local))
                {
                    OpenFiles(new[] { local });
                }

                return;
            }

            if (uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, VirtualHosts.App, StringComparison.OrdinalIgnoreCase))
            {
                // A relative link resolved against the viewer page: treat it as document-relative.
                HandleLink(uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped).TrimStart('/'));
                return;
            }

            HandleLink(url);
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _pageReady = false;
                BeginInvoke(() =>
                {
                    MessageBox.Show(this, "The viewer component stopped unexpectedly. Please restart JustMd.",
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                });
                return;
            }

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
                || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                _pageReady = false;
                BeginInvoke(() => Core?.Navigate(VirtualHosts.ViewerUrl + "?theme=" + _palette.CssName));
            }
        }

        // ---------- context menu: Copy / Select all, plus editing commands in the editor ----------

        private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            IList<CoreWebView2ContextMenuItem> items = e.MenuItems;
            bool editable = e.ContextMenuTarget.IsEditable;
            bool hasSelectAll = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                string name = items[i].Name;
                if (name == "selectAll")
                {
                    hasSelectAll = true;
                }
                else if (name != "copy" && !(editable && name is "cut" or "paste" or "undo" or "redo"))
                {
                    items.RemoveAt(i);
                }
            }

            if (!hasSelectAll && Core != null && !editable)
            {
                CoreWebView2ContextMenuItem selectAll = Core.Environment.CreateContextMenuItem(
                    "Select all", null, CoreWebView2ContextMenuItemKind.Command);
                selectAll.CustomItemSelected += (_, _) => Post(new HostMessage { Type = "selectAll" });
                items.Add(selectAll);
            }

            if (items.Count == 0)
            {
                e.Handled = true;
            }
        }

        // ---------- page messages ----------

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsViewerUrl(e.Source))
            {
                return;
            }

            string json;
            try
            {
                json = e.WebMessageAsJson;
            }
            catch (ArgumentException)
            {
                return;
            }

            WebMessage? message = WebMessageParser.Parse(json);
            if (message == null)
            {
                return;
            }

            IReadOnlyList<string> droppedPaths = Array.Empty<string>();
            if (message.Type == WebMessageType.Drop)
            {
                // Additional objects are only valid during this event.
                droppedPaths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path)
                    .Where(p => !string.IsNullOrEmpty(p)).Take(InstanceProtocol.MaxFiles).ToList()
                    ?? (IReadOnlyList<string>)Array.Empty<string>();
            }

            // Leave the WebView2 event before showing any dialog.
            BeginInvoke(() => HandleWebMessage(message, droppedPaths));
        }

        private void HandleWebMessage(WebMessage message, IReadOnlyList<string> droppedPaths)
        {
            if (IsDisposed)
            {
                return;
            }

            switch (message.Type)
            {
                case WebMessageType.Ready:
                    _pageReady = true;
                    bool editsLost = ResetEditorsForNewPage();
                    PostTheme();
                    Post(new HostMessage { Type = "outline", Visible = _settings.OutlineVisible, Width = _settings.OutlineWidth });
                    PostTabs();
                    ShowActiveTab(null);
                    if (editsLost)
                    {
                        ShowEditError("The viewer component restarted and unsaved changes were lost.");
                    }

                    break;

                case WebMessageType.Open:
                    ShowOpenDialog();
                    break;

                case WebMessageType.Copy:
                    Post(new HostMessage { Type = "copied", Id = message.Id, Ok = TrySetClipboard(message.Text ?? string.Empty) });
                    break;

                case WebMessageType.Link:
                    HandleLink(message.Href);
                    break;

                case WebMessageType.CycleTheme:
                    CycleTheme();
                    break;

                case WebMessageType.Outline:
                    _settings.OutlineVisible = message.Visible;
                    ScheduleSave();
                    break;

                case WebMessageType.OutlineWidth:
                    _settings.OutlineWidth = AppSettings.ClampOutlineWidth(message.Width);
                    ScheduleSave();
                    break;

                case WebMessageType.ActivateTab:
                    ActivateTab(_tabs.FindById(message.TabId));
                    break;

                case WebMessageType.CloseTab:
                    CloseTab(_tabs.FindById(message.TabId));
                    break;

                case WebMessageType.TabState:
                    DocumentTab? reported = _tabs.FindById(message.TabId);
                    if (reported != null && message.View != null)
                    {
                        reported.View = message.View;
                        SessionChanged();
                    }

                    break;

                case WebMessageType.Drop:
                    List<string> files = droppedPaths.Where(File.Exists).ToList();
                    if (_tabs.Active is DocumentTab { IsEditing: true } dropTarget)
                    {
                        foreach (string image in files.Where(LocalImageUrls.IsImageExtension))
                        {
                            PostImagePicked(dropTarget, image);
                        }

                        files.RemoveAll(LocalImageUrls.IsImageExtension);
                    }

                    if (files.Count > 0)
                    {
                        OpenFiles(files);
                    }

                    break;

                case WebMessageType.NewFile:
                    NewFile();
                    break;

                case WebMessageType.EnterEdit:
                    if (_tabs.FindById(message.TabId) is DocumentTab toEdit)
                    {
                        RunEditFlow(() => EnterEditAsync(toEdit));
                    }

                    break;

                case WebMessageType.ExitEdit:
                    if (_tabs.FindById(message.TabId) is DocumentTab toExit)
                    {
                        RunEditFlow(() => ExitEditAsync(toExit));
                    }

                    break;

                case WebMessageType.Save:
                    SaveTab(_tabs.FindById(message.TabId));
                    break;

                case WebMessageType.SaveAs:
                    SaveTabAs(_tabs.FindById(message.TabId));
                    break;

                case WebMessageType.EditorDirty:
                    if (_tabs.FindById(message.TabId) is DocumentTab { IsEditing: true } changed)
                    {
                        SetDirty(changed, message.Dirty);
                    }

                    break;

                case WebMessageType.EditorContent:
                    CompleteContentRequest(message);
                    break;

                case WebMessageType.PickImage:
                    if (_tabs.FindById(message.TabId) is DocumentTab { IsEditing: true } picking)
                    {
                        ShowImageDialog(picking);
                    }

                    break;

                case WebMessageType.ReloadFromDisk:
                    if (_tabs.FindById(message.TabId) is DocumentTab toReload)
                    {
                        RunEditFlow(() => ReloadFromDiskAsync(toReload));
                    }

                    break;
            }
        }

        private void Post(HostMessage message)
        {
            if (!_pageReady || Core == null)
            {
                return;
            }

            try
            {
                Core.PostWebMessageAsJson(message.ToJson());
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is COMException)
            {
                // Page is reloading; it will ask for state again with "ready".
            }
        }

        private void PostTheme() => Post(new HostMessage
        {
            Type = "theme",
            Theme = _palette.CssName,
            Mode = _settings.Theme.ToString().ToLowerInvariant(),
        });

        private static bool TrySetClipboard(string text)
        {
            try
            {
                if (text.Length == 0)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, retryTimes: 10, retryDelay: 50);
                }

                return true;
            }
            catch (Exception ex) when (ex is ExternalException || ex is System.Threading.ThreadStateException)
            {
                return false;
            }
        }

        // ---------- links ----------

        private void HandleLink(string? href)
        {
            LinkTarget target = LinkClassifier.Classify(href, _tabs.Active?.Path);
            switch (target.Kind)
            {
                case LinkKind.None:
                    break;

                case LinkKind.Anchor:
                    Post(new HostMessage { Type = "scrollTo", Fragment = target.Fragment ?? string.Empty });
                    break;

                case LinkKind.External:
                    ConfirmExternalLink(target.Uri!);
                    break;

                case LinkKind.LocalMarkdown:
                    if (string.Equals(target.LocalPath, _tabs.Active?.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        Post(new HostMessage { Type = "scrollTo", Fragment = target.Fragment ?? string.Empty });
                    }
                    else
                    {
                        OpenFiles(new[] { target.LocalPath! }, target.Fragment);
                    }

                    break;

                case LinkKind.LocalFile:
                    string path = target.LocalPath ?? target.Href;
                    bool exists = File.Exists(path) || Directory.Exists(path);
                    MessageBox.Show(this,
                        exists
                            ? "For your safety, JustMd only follows links to Markdown files. This link points to:\n\n" + path
                            : "This link points to a file that does not exist:\n\n" + path,
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;

                default:
                    string shown = target.Href.Length > 300 ? target.Href.Substring(0, 300) + "…" : target.Href;
                    MessageBox.Show(this,
                        (target.Scheme != null
                            ? "This link was blocked. JustMd only opens web (http, https) and email (mailto) links, not \"" + target.Scheme + ":\" links."
                            : "This link was blocked because it points to a location JustMd does not open.")
                        + "\n\n" + shown,
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }

        private void ConfirmExternalLink(Uri uri)
        {
            using var dialog = new ExternalLinkDialog(uri, _palette, DeviceDpi);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            if (dialog.Choice == ExternalLinkChoice.Copy)
            {
                TrySetClipboard(uri.AbsoluteUri);
            }
            else if (dialog.Choice == ExternalLinkChoice.Open)
            {
                OpenExternal(uri.AbsoluteUri);
            }
        }

        /// <summary>Last gate before the shell: re-validate the scheme allow-list, then open.</summary>
        private void OpenExternal(string url)
        {
            if (!LinkClassifier.TryValidateExternal(url, out Uri? uri))
            {
                MessageBox.Show(this, "This link was refused because it is not a web (http, https) or email (mailto) link.",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Program.TryOpenWithShell(uri.AbsoluteUri))
            {
                MessageBox.Show(this, "The link could not be opened. Is a default browser or mail app set up?",
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- local images ----------

        private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            CoreWebView2? core = Core;
            if (core == null)
            {
                return;
            }

            CoreWebView2Environment environment = core.Environment;
            // Policy is checked against the open documents: the active tab first, but any open tab
            // is accepted so a request that arrives just after a tab switch is still answered.
            if (e.Request.Method != "GET"
                || !LocalImageUrls.TryGetPath(e.Request.Uri, out string path)
                || !IsServableForOpenTabs(path))
            {
                e.Response = NotFound(environment);
                return;
            }

            CoreWebView2Deferral deferral = e.GetDeferral();
            try
            {
                byte[]? bytes = await Task.Run(() => TryReadImage(path));
                e.Response = bytes == null
                    ? NotFound(environment)
                    : environment.CreateWebResourceResponse(
                        new MemoryStream(bytes, writable: false), 200, "OK",
                        "Content-Type: " + LocalImageUrls.GetMimeType(path) + "\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "X-Content-Type-Options: nosniff\r\n" +
                        "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is COMException || ex is ObjectDisposedException)
            {
                // WebView is shutting down.
            }
            finally
            {
                deferral.Complete();
            }
        }

        private bool IsServableForOpenTabs(string path) =>
            (_tabs.Active != null && LocalImageUrls.IsServable(path, _tabs.Active.Path))
            || _tabs.Tabs.Any(t => LocalImageUrls.IsServable(path, t.Path));

        private static CoreWebView2WebResourceResponse NotFound(CoreWebView2Environment environment) =>
            environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");

        private static byte[]? TryReadImage(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxImageBytes)
                {
                    return null;
                }

                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
