using System;
using System.Collections.Generic;
using System.Threading;
using JustMd.Core.Spelling;

namespace JustMd.Spelling
{
    /// <summary>
    /// Runs the spell checker on its own MTA thread, which starts the first time spelling is
    /// needed and makes every COM call. Results come back on the UI thread. A spelling failure
    /// costs at most some underlines, so nothing that goes wrong here may reach the window: the
    /// worker catches everything, out-of-memory from COM included, because the app may hold unsaved edits.
    /// </summary>
    internal sealed class SpellingService : IDisposable
    {
        private const int AvailabilityWaitMilliseconds = 3000;

        private readonly Func<ISpellingEngine> _createEngine;
        private readonly Func<IReadOnlyList<string>> _readLanguages;
        private readonly Action<Action> _toUiThread;
        private readonly object _gate = new();
        private readonly SpellingWorkQueue<Action<Func<bool>>> _queue = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly HashSet<string> _languages = new(StringComparer.Ordinal);
        private Thread? _thread;
        private ISpellingEngine? _engine;
        private DocumentSpeller? _speller;
        private bool _disposed;

        /// <param name="createEngine">Called on the worker thread.</param>
        /// <param name="readLanguages">Called on the worker thread.</param>
        /// <param name="toUiThread">Runs an action on the UI thread later, or drops it if the window is gone.</param>
        public SpellingService(Func<ISpellingEngine> createEngine, Func<IReadOnlyList<string>> readLanguages, Action<Action> toUiThread)
        {
            _createEngine = createEngine;
            _readLanguages = readLanguages;
            _toUiThread = toUiThread;
        }

        /// <summary>
        /// Whether any language can be checked. The first call waits for the worker to find out,
        /// but not for long: a stuck spell checker must not freeze the window, and it reads false until it answers.
        /// </summary>
        public bool IsAvailable => EnsureStarted() && _ready.Wait(AvailabilityWaitMilliseconds) && _speller != null;

        /// <summary>Checks blocks of text; scans wait behind everything else. <paramref name="done"/> gets one list per block, on the UI thread.</summary>
        public void Check(int tabId, bool scan, IReadOnlyList<string> blocks, Action<IReadOnlyList<IReadOnlyList<SpellingError>>> done)
        {
            Enqueue(tabId, urgent: !scan, isCurrent =>
            {
                var results = new List<IReadOnlyList<SpellingError>>(blocks.Count);
                foreach (string block in blocks)
                {
                    if (!isCurrent())
                    {
                        return;
                    }

                    results.Add(CheckBlock(block));
                }

                Deliver(isCurrent, () => done(results));
            });
        }

        /// <summary><paramref name="done"/> gets the suggestions on the UI thread, none if the language is not in use.</summary>
        public void Suggest(int tabId, string language, string word, Action<IReadOnlyList<string>> done)
        {
            Enqueue(tabId, urgent: true, isCurrent =>
            {
                IReadOnlyList<string> suggestions = Array.Empty<string>();
                try
                {
                    if (_engine != null && _languages.Contains(language))
                    {
                        suggestions = _engine.Suggest(language, word);
                    }
                }
                catch (Exception)
                {
                    // The page shows an empty list.
                }

                Deliver(isCurrent, () => done(suggestions));
            });
        }

        /// <summary>The user asked for this, so it still happens if the tab closes right after.</summary>
        public void Ignore(string language, string word) =>
            EnqueueWordCommand(language, engine => engine.Ignore(language, word));

        /// <summary>The user asked for this, so it still happens if the tab closes right after.</summary>
        public void Add(string language, string word) =>
            EnqueueWordCommand(language, engine => engine.Add(language, word));

        /// <summary>Drops the tab's queued work, and any result of it that is not delivered yet.</summary>
        public void Forget(int tabId)
        {
            lock (_gate)
            {
                _queue.Forget(tabId);
            }
        }

        /// <summary>Does not wait: a check can run for seconds and the thread is a background thread.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _queue.Clear();
                Monitor.PulseAll(_gate);
            }
        }

        private void EnqueueWordCommand(string language, Action<ISpellingEngine> command)
        {
            Enqueue(SpellingWorkQueue<Action<Func<bool>>>.NoTab, urgent: true, _ =>
            {
                if (_engine != null && _languages.Contains(language))
                {
                    command(_engine);
                }
            });
        }

        private void Enqueue(int tabId, bool urgent, Action<Func<bool>> work)
        {
            if (!EnsureStarted())
            {
                return;
            }

            lock (_gate)
            {
                _queue.Enqueue(tabId, urgent, work);
                Monitor.PulseAll(_gate);
            }
        }

        private bool EnsureStarted()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_thread != null)
                {
                    return true;
                }

                try
                {
                    var thread = new Thread(Run) { IsBackground = true, Name = "JustMd spelling" };
                    thread.SetApartmentState(ApartmentState.MTA);
                    thread.Start();
                    _thread = thread;
                    return true;
                }
                catch (Exception)
                {
                    _disposed = true; // no thread, no spelling
                    return false;
                }
            }
        }

        private void Run()
        {
            try
            {
                Initialize();
            }
            catch (Exception)
            {
                _speller = null;
            }
            finally
            {
                _ready.Set();
            }

            while (TryTake(out SpellingWorkQueue<Action<Func<bool>>>.Item item))
            {
                try
                {
                    item.Work(() => IsCurrent(item));
                }
                catch (Exception)
                {
                    // The next request may well work.
                }
            }

            try
            {
                _engine?.Dispose();
            }
            catch (Exception)
            {
                // The process is on its way out.
            }
        }

        private void Initialize()
        {
            ISpellingEngine engine = _createEngine();
            _engine = engine;
            IReadOnlyDictionary<TextScript, string> assigned = SpellingLanguages.Assign(_readLanguages(), engine.IsSupported);
            _languages.UnionWith(assigned.Values);
            if (assigned.Count > 0)
            {
                _speller = new DocumentSpeller(engine, assigned);
            }
        }

        private bool TryTake(out SpellingWorkQueue<Action<Func<bool>>>.Item item)
        {
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed)
                    {
                        item = default;
                        return false;
                    }

                    if (_queue.TryDequeue(out item))
                    {
                        return true;
                    }

                    Monitor.Wait(_gate);
                }
            }
        }

        private bool IsCurrent(SpellingWorkQueue<Action<Func<bool>>>.Item item)
        {
            lock (_gate)
            {
                return !_disposed && _queue.IsCurrent(item);
            }
        }

        private IReadOnlyList<SpellingError> CheckBlock(string block)
        {
            try
            {
                return _speller?.Check(block) ?? Array.Empty<SpellingError>();
            }
            catch (Exception)
            {
                // The page still gets an answer for this block, so it can go on to the next request.
                return Array.Empty<SpellingError>();
            }
        }

        private void Deliver(Func<bool> isCurrent, Action done)
        {
            try
            {
                _toUiThread(() =>
                {
                    try
                    {
                        // Checked here as well: the tab can be forgotten while this waits for the UI thread.
                        if (isCurrent())
                        {
                            done();
                        }
                    }
                    catch (Exception)
                    {
                        // An exception on the UI thread would bring up the crash dialog.
                    }
                });
            }
            catch (Exception)
            {
                // The window is closing.
            }
        }
    }
}
