using System;
using System.IO;
using System.Windows.Forms;

namespace JustMd
{
    /// <summary>
    /// Watches one file and raises <see cref="Changed"/> on the UI thread, debounced, after it
    /// is modified or replaced (editors often save via a temp file + rename).
    /// </summary>
    internal sealed class DocumentWatcher : IDisposable
    {
        private readonly string _path;
        private readonly Control _owner;
        private readonly System.Windows.Forms.Timer _debounce;
        private FileSystemWatcher? _watcher;
        private bool _disposed;

        public DocumentWatcher(string path, Control owner, int debounceMilliseconds = 300)
        {
            _path = path;
            _owner = owner;
            _debounce = new System.Windows.Forms.Timer { Interval = debounceMilliseconds };
            _debounce.Tick += OnDebounceTick;
            Start();
        }

        public event EventHandler? Changed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _debounce.Dispose();
        }

        private void Start()
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is PlatformNotSupportedException)
            {
                // Some network or virtual drives cannot be watched; F5 still works.
                _watcher = null;
            }
        }

        private void Stop()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, _path, StringComparison.OrdinalIgnoreCase))
            {
                PostToUi(RestartDebounce);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow or the folder went away: re-create the watcher and refresh once.
            PostToUi(() =>
            {
                Stop();
                Start();
                RestartDebounce();
            });
        }

        private void PostToUi(Action action)
        {
            if (_disposed || _owner.IsDisposed || !_owner.IsHandleCreated)
            {
                return;
            }

            try
            {
                _owner.BeginInvoke(() =>
                {
                    if (!_disposed)
                    {
                        action();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Window is closing.
            }
        }

        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnDebounceTick(object? sender, EventArgs e)
        {
            _debounce.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
