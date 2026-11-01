using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace JustMd.Core.Settings
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> as JSON. Loading never throws: a missing,
    /// empty or corrupt file yields defaults. Saving writes a uniquely named temp file and swaps
    /// it in. Several app windows (separate processes) can share the file: use
    /// <see cref="SaveChanges"/> so each one only writes the values it actually changed.
    /// </summary>
    public sealed class SettingsStore
    {
        private const int MaxFileBytes = 1024 * 1024;

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public SettingsStore(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public string FilePath { get; }

        /// <summary>Default location: <c>%LOCALAPPDATA%\JustMd\settings.json</c>.</summary>
        public static string DefaultFilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

        public AppSettings Load() => TryRead(out AppSettings? settings) == ReadResult.Ok ? settings! : new AppSettings();

        /// <summary>Saves all values, replacing whatever is on disk. Returns false if the file could not be written.</summary>
        public bool Save(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            using (AcquireLock())
            {
                return Write(settings);
            }
        }

        /// <summary>
        /// Merge-save for one of possibly several running windows: re-reads the file and applies
        /// only the values where <paramref name="current"/> differs from <paramref name="baseline"/>
        /// (the snapshot this window last loaded or saved), so concurrent windows don't undo each
        /// other's changes. Returns false if the file could not be written.
        /// </summary>
        public bool SaveChanges(AppSettings current, AppSettings baseline)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(baseline);

            using (AcquireLock())
            {
                ReadResult result = ReadResult.Failed;
                AppSettings? onDisk = null;
                for (int attempt = 0; attempt < 3 && (result = TryRead(out onDisk)) == ReadResult.Failed; attempt++)
                {
                    Thread.Sleep(50);
                }

                // Missing file: start from defaults. Unreadable/corrupt: our own view is the best we have.
                AppSettings merged = result switch
                {
                    ReadResult.Ok => onDisk!,
                    ReadResult.Missing => new AppSettings(),
                    _ => current.Clone(),
                };

                if (current.Theme != baseline.Theme)
                {
                    merged.Theme = current.Theme;
                }

                if (!current.Zoom.Equals(baseline.Zoom))
                {
                    merged.Zoom = current.Zoom;
                }

                if (current.OutlineVisible != baseline.OutlineVisible)
                {
                    merged.OutlineVisible = current.OutlineVisible;
                }

                if (current.OutlineWidth != baseline.OutlineWidth)
                {
                    merged.OutlineWidth = current.OutlineWidth;
                }

                if (!WindowPlacement.AreEqual(current.Window, baseline.Window))
                {
                    merged.Window = current.Window?.Clone();
                }

                if (!SessionState.AreEqual(current.Session, baseline.Session))
                {
                    merged.Session = current.Session.Clone();
                }

                return Write(merged);
            }
        }

        public static string Serialize(AppSettings settings)
        {
            AppSettings copy = settings.Clone();
            copy.Normalize();
            return JsonSerializer.Serialize(copy, JsonOptions);
        }

        private enum ReadResult
        {
            Ok,
            Missing,
            Failed,
        }

        private ReadResult TryRead(out AppSettings? settings)
        {
            settings = null;
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists)
                {
                    return ReadResult.Missing;
                }

                if (info.Length == 0 || info.Length > MaxFileBytes)
                {
                    return ReadResult.Failed;
                }

                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(FilePath), JsonOptions);
                if (settings == null)
                {
                    return ReadResult.Failed;
                }

                settings.Normalize();
                return ReadResult.Ok;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is JsonException || ex is NotSupportedException
                                       || ex is ArgumentException)
            {
                settings = null;
                return ReadResult.Failed;
            }
        }

        private bool Write(AppSettings settings)
        {
            // Unique temp name: two processes saving at once never share a temp file.
            string tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, Serialize(settings), new UTF8Encoding(false));
                File.Move(tempPath, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                TryDelete(tempPath);
                return false;
            }
        }

        /// <summary>Cross-process lock around read-merge-write; best effort (never blocks for long).</summary>
        private IDisposable AcquireLock()
        {
            Mutex? mutex = null;
            try
            {
                string name = @"Local\JustMd.Settings." + StableHash(Path.GetFullPath(FilePath).ToUpperInvariant());
                mutex = new Mutex(false, name);
                bool owned;
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                return new MutexLease(mutex, owned);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is WaitHandleCannotBeOpenedException || ex is ArgumentException
                                       || ex is PlatformNotSupportedException)
            {
                mutex?.Dispose();
                return new MutexLease(null, false);
            }
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value)
                {
                    hash = (hash ^ c) * 16777619;
                }

                return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private sealed class MutexLease : IDisposable
        {
            private readonly Mutex? _mutex;
            private readonly bool _owned;

            public MutexLease(Mutex? mutex, bool owned)
            {
                _mutex = mutex;
                _owned = owned;
            }

            public void Dispose()
            {
                if (_mutex == null)
                {
                    return;
                }

                if (_owned)
                {
                    _mutex.ReleaseMutex();
                }

                _mutex.Dispose();
            }
        }
    }
}
