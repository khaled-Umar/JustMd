using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using JustMd.Core;
using JustMd.Core.Messaging;
using JustMd.Core.Settings;
using JustMd.Core.Tabs;
using Microsoft.Web.WebView2.WinForms;

namespace JustMd
{
    /// <summary>
    /// The single application window: one WebView2 that fills the client area and shows the
    /// active tab. All chrome (tab strip, outline, buttons) is HTML inside the viewer page; this
    /// class owns the tabs, settings, theme, keyboard shortcuts and every security decision.
    /// </summary>
    internal sealed partial class MainForm : Form
    {
        private static readonly double[] ZoomSteps =
            { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0 };

        private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultFilePath);
        private readonly AppSettings _settings;
        private readonly MarkdownRenderer _renderer = new();
        private readonly WebView2 _webView;
        private readonly System.Windows.Forms.Timer _saveTimer;
        private readonly ConcurrentQueue<IReadOnlyList<string>> _pendingFromOtherInstance = new();
        private AppSettings _settingsBaseline;
        private ThemePalette _palette;

        public MainForm(IReadOnlyList<string> files)
        {
            _settings = _settingsStore.Load();
            _settingsBaseline = _settings.Clone();
            _palette = ThemePalette.For(_settings.Theme);

            Text = Program.AppName;
            Icon = LoadAppIcon();
            BackColor = _palette.Background;
            MinimumSize = new Size(ScaleForDpi(480), ScaleForDpi(360));
            ApplySavedPlacement();

            _saveTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _saveTimer.Tick += (_, _) => SaveSettingsNow();

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = _palette.Background,
            };

            // While the WebView has focus, accelerator keys arrive as the control's KeyDown
            // (not via the form's ProcessCmdKey); Handled = true stops the browser default.
            _webView.KeyDown += (_, e) =>
            {
                if (HandleShortcut(e.KeyData))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_webView);

            RestoreStartupTabs(files);
        }

        /// <summary>Called on a thread-pool thread when another process hands over its files.</summary>
        public void ReceiveFilesFromOtherInstance(IReadOnlyList<string> files)
        {
            _pendingFromOtherInstance.Enqueue(files);
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(DrainOtherInstanceQueue);
                }
                catch (InvalidOperationException)
                {
                    // Window is closing.
                }
            }
        }

        private void DrainOtherInstanceQueue()
        {
            bool any = false;
            while (_pendingFromOtherInstance.TryDequeue(out IReadOnlyList<string>? files))
            {
                any = true;
                if (files.Count > 0)
                {
                    OpenFiles(files);
                }
            }

            if (any)
            {
                BringToForeground();
            }
        }

        private void BringToForeground()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                NativeMethods.Restore(Handle);
            }

            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            _webView.Focus(); // so keyboard shortcuts work straight away
        }

        // ---------- window lifecycle ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetDarkTitleBar(Handle, _palette.IsDark, repaint: false);
            if (!_pendingFromOtherInstance.IsEmpty)
            {
                BeginInvoke(DrainOtherInstanceQueue);
            }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await InitializeWebViewAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel)
            {
                return;
            }

            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.Window = new WindowPlacement
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == FormWindowState.Maximized,
            };
            CaptureSession();
            SaveSettingsNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (DocumentTab tab in _tabs.Tabs)
                {
                    tab.Watcher?.Dispose();
                    tab.Watcher = null;
                }

                _saveTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Windows broadcasts "ImmersiveColorSet" when the light/dark app mode changes.
            if (m.Msg == NativeMethods.WM_SETTINGCHANGE && m.LParam != IntPtr.Zero
                && _settings.Theme == ThemeMode.System
                && string.Equals(Marshal.PtrToStringUni(m.LParam), "ImmersiveColorSet", StringComparison.Ordinal))
            {
                BeginInvoke(ApplyTheme);
            }
        }

        private static Icon? LoadAppIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is ExternalException)
            {
                return null;
            }
        }

        private int ScaleForDpi(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

        private void ApplySavedPlacement()
        {
            WindowPlacement? saved = _settings.Window;
            if (saved != null)
            {
                var rect = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
                bool visible = Screen.AllScreens.Any(screen =>
                {
                    Rectangle overlap = Rectangle.Intersect(screen.WorkingArea, rect);
                    return overlap.Width >= 120 && overlap.Height >= 80;
                });

                if (visible)
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = rect;
                    if (saved.Maximized)
                    {
                        WindowState = FormWindowState.Maximized;
                    }

                    return;
                }
            }

            Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            Size = new Size(
                Math.Min(ScaleForDpi(1100), area.Width - ScaleForDpi(40)),
                Math.Min(ScaleForDpi(820), area.Height - ScaleForDpi(40)));
            StartPosition = FormStartPosition.CenterScreen;
        }

        // ---------- settings ----------

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Writes only what this window changed. Normally there is a single instance, but if a
        /// hand-off ever fails two windows can exist, and neither should undo the other's changes.
        /// </summary>
        private void SaveSettingsNow()
        {
            _saveTimer.Stop();
            if (_settingsStore.SaveChanges(_settings, _settingsBaseline))
            {
                _settingsBaseline = _settings.Clone();
            }
        }

        // ---------- keyboard ----------

        /// <summary>Fallback for keys pressed while focus is outside the WebView.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        /// <summary>
        /// Single place for keyboard shortcuts. Returns true when the key is ours, which also
        /// stops the browser from acting on it. The action runs after the key event returns so
        /// dialogs or closing the window never happen inside a WebView2 callback.
        /// </summary>
        private bool HandleShortcut(Keys keyData)
        {
            Action? action = GetShortcutAction(keyData);
            if (action == null)
            {
                return false;
            }

            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(action);
            }

            return true;
        }

        private Action? GetShortcutAction(Keys keyData)
        {
            if ((keyData & Keys.Modifiers) == Keys.Control)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D9)
                {
                    int number = key - Keys.D0;
                    return () => ActivateTab(_tabs.ByNumber(number));
                }

                if (key >= Keys.NumPad1 && key <= Keys.NumPad9)
                {
                    int number = key - Keys.NumPad0;
                    return () => ActivateTab(_tabs.ByNumber(number));
                }
            }

            switch (keyData)
            {
                case Keys.Control | Keys.O:
                    return ShowOpenDialog;

                case Keys.F5:
                case Keys.Control | Keys.R:
                case Keys.Shift | Keys.F5:
                case Keys.Control | Keys.F5:
                case Keys.Control | Keys.Shift | Keys.R:
                case Keys.BrowserRefresh:
                    return ReloadActiveTab;

                case Keys.Control | Keys.W:
                case Keys.Control | Keys.F4:
                    return () => CloseTab(_tabs.Active);

                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown:
                    return () => ActivateTab(_tabs.Cycle(+1));

                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp:
                    return () => ActivateTab(_tabs.Cycle(-1));

                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.Oemplus:
                case Keys.Control | Keys.Add:
                    return () => StepZoom(+1);

                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Shift | Keys.OemMinus:
                case Keys.Control | Keys.Subtract:
                    return () => StepZoom(-1);

                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0:
                    return () => StepZoom(0);

                case Keys.Control | Keys.P:
                    return Print;

                case Keys.Control | Keys.T:
                    return CycleTheme;

                case Keys.Control | Keys.Shift | Keys.O:
                    return () => Post(new HostMessage { Type = "toggleOutline" });

                // Browser shortcuts that make no sense in a document viewer: swallowed.
                case Keys.Alt | Keys.Left:
                case Keys.Alt | Keys.Right:
                case Keys.Alt | Keys.Home:
                case Keys.BrowserBack:
                case Keys.BrowserForward:
                case Keys.BrowserHome:
                case Keys.BrowserStop:
                case Keys.BrowserSearch:
                case Keys.BrowserFavorites:
                case Keys.Control | Keys.S:
                case Keys.Control | Keys.Shift | Keys.S:
                case Keys.Control | Keys.U:
                case Keys.Control | Keys.N:
                case Keys.Control | Keys.Shift | Keys.N:
                case Keys.Control | Keys.Shift | Keys.T:
                case Keys.Control | Keys.H:
                case Keys.Control | Keys.J:
                case Keys.Control | Keys.D:
                case Keys.Control | Keys.L:
                case Keys.Control | Keys.E:
                case Keys.Control | Keys.K:
                case Keys.Control | Keys.Shift | Keys.Delete:
                case Keys.Control | Keys.Shift | Keys.I:
                case Keys.Control | Keys.Shift | Keys.J:
                case Keys.Control | Keys.Shift | Keys.C:
                case Keys.Control | Keys.Shift | Keys.K:
                case Keys.Control | Keys.Shift | Keys.P:
                case Keys.Control | Keys.Shift | Keys.Y:
                case Keys.F7:
                case Keys.F12:
                    return static () => { };

                default:
                    return null;
            }
        }

        // ---------- commands ----------

        private void ShowOpenDialog()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Open Markdown files",
                Filter = MarkdownFiles.DialogFilter,
                CheckFileExists = true,
                Multiselect = true,
                RestoreDirectory = true,
            };

            string? folder = _tabs.Active == null ? null : Path.GetDirectoryName(_tabs.Active.Path);
            if (folder != null && Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                OpenFiles(dialog.FileNames);
            }
        }

        private void StepZoom(int direction)
        {
            double current = _webView.ZoomFactor;
            double next = direction == 0
                ? AppSettings.DefaultZoom
                : direction > 0
                    ? ZoomSteps.Where(z => z > current + 0.001).DefaultIfEmpty(AppSettings.MaxZoom).First()
                    : ZoomSteps.Where(z => z < current - 0.001).DefaultIfEmpty(AppSettings.MinZoom).Last();
            _webView.ZoomFactor = AppSettings.ClampZoom(next);
        }

        private void Print()
        {
            try
            {
                _webView.CoreWebView2?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is COMException)
            {
                // Print UI could not be shown (e.g. already open).
            }
        }

        private void CycleTheme()
        {
            _settings.Theme = _settings.Theme switch
            {
                ThemeMode.System => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
            ScheduleSave();
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            if (IsDisposed)
            {
                return;
            }

            _palette = ThemePalette.For(_settings.Theme);
            BackColor = _palette.Background;
            _webView.DefaultBackgroundColor = _palette.Background;
            if (IsHandleCreated)
            {
                NativeMethods.SetDarkTitleBar(Handle, _palette.IsDark, repaint: true);
            }

            ApplyWebViewColorScheme();
            PostTheme();
        }
    }
}
