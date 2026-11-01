using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using JustMd.Core.Messaging;
using JustMd.Core.Spelling;

namespace JustMd.Spelling
{
    /// <summary>
    /// The Windows Spell Checking API. Without the API the engine supports no language. Only
    /// <see cref="Check"/> throws when it fails, because no errors would read as "all words are
    /// fine". A failing HRESULT can surface as many exception types (E_INVALIDARG is an
    /// ArgumentException), so every other call catches them all.
    /// </summary>
    internal sealed class WindowsSpellingEngine : ISpellingEngine
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ISpellChecker?> _checkers = new(StringComparer.Ordinal);
        private ISpellCheckerFactory? _factory;
        private bool _factoryTried;
        private bool _disposed;

        public bool IsSupported(string language)
        {
            lock (_gate)
            {
                try
                {
                    return Factory() is ISpellCheckerFactory factory && factory.IsSupported(language) != 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public IReadOnlyList<SpellingError> Check(string language, string text)
        {
            var errors = new List<SpellingError>();
            lock (_gate)
            {
                IEnumSpellingError? found = null;
                try
                {
                    ISpellChecker checker = Checker(language) ?? throw new InvalidOperationException("No spell checker for " + language);
                    found = checker.Check(text);
                    while (found != null)
                    {
                        // S_FALSE ends the list; a failure must not look like the end of it.
                        int result = found.Next(out ISpellingError? error);
                        Marshal.ThrowExceptionForHR(result);
                        if (result != 0 || error == null)
                        {
                            break;
                        }

                        try
                        {
                            int action = error.get_CorrectiveAction();
                            if (action != SpellingError.ActionNone)
                            {
                                errors.Add(new SpellingError(
                                    ToInt(error.get_StartIndex()),
                                    ToInt(error.get_Length()),
                                    action,
                                    action == SpellingError.ActionReplace ? error.get_Replacement() : null));
                            }
                        }
                        finally
                        {
                            Release(error);
                        }
                    }
                }
                finally
                {
                    Release(found);
                }
            }

            return errors;
        }

        public IReadOnlyList<string> Suggest(string language, string word)
        {
            var suggestions = new List<string>();
            lock (_gate)
            {
                IEnumString? found = null;
                try
                {
                    found = Checker(language)?.Suggest(word);
                    while (found != null && suggestions.Count < WebMessageParser.MaxSpellingSuggestions
                        && found.Next(1, out string? suggestion, out int fetched) == 0 && fetched == 1)
                    {
                        // A word the checker accepts comes back as its own only suggestion.
                        if (!string.IsNullOrEmpty(suggestion) && suggestion != word)
                        {
                            suggestions.Add(suggestion);
                        }
                    }
                }
                catch (Exception)
                {
                    // Keep what was found before the failure.
                }
                finally
                {
                    Release(found);
                }
            }

            return suggestions;
        }

        public void Ignore(string language, string word)
        {
            lock (_gate)
            {
                try
                {
                    Checker(language)?.Ignore(word);
                }
                catch (Exception)
                {
                    // The word simply stays flagged.
                }
            }
        }

        public void Add(string language, string word)
        {
            lock (_gate)
            {
                try
                {
                    Checker(language)?.Add(word);
                }
                catch (Exception)
                {
                    // The word simply stays flagged.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (ISpellChecker? checker in _checkers.Values)
                {
                    Release(checker);
                }

                _checkers.Clear();
                Release(_factory);
                _factory = null;
            }
        }

        private ISpellCheckerFactory? Factory()
        {
            if (!_factoryTried && !_disposed)
            {
                _factoryTried = true;
                try
                {
                    _factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                }
                catch (Exception)
                {
                    _factory = null; // not on this edition of Windows
                }
            }

            return _factory;
        }

        private ISpellChecker? Checker(string language)
        {
            if (_checkers.TryGetValue(language, out ISpellChecker? checker))
            {
                return checker;
            }

            // A failure is remembered: it would only fail again on every block.
            try
            {
                checker = Factory()?.CreateSpellChecker(language);
            }
            catch (Exception)
            {
                checker = null;
            }

            if (!_disposed)
            {
                _checkers[language] = checker;
            }

            return checker;
        }

        private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

        private static void Release(object? comObject)
        {
            try
            {
                if (comObject != null)
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception)
            {
                // Already gone; the finalizer has nothing left to do either.
            }
        }

        // The order of the methods is the vtable: every slot must be declared, used or not.

        [ComImport]
        [Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
        private class SpellCheckerFactoryClass
        {
        }

        [ComImport]
        [Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellCheckerFactory
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumString get_SupportedLanguages();

            int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

            [return: MarshalAs(UnmanagedType.Interface)]
            ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        }

        [ComImport]
        [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellChecker
        {
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string get_LanguageTag();

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);

            void Add([MarshalAs(UnmanagedType.LPWStr)] string word);

            void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);

            void AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);

            byte GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId);

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumString get_OptionIds();

            [return: MarshalAs(UnmanagedType.LPWStr)]
            string get_Id();

            [return: MarshalAs(UnmanagedType.LPWStr)]
            string get_LocalizedName();

            uint add_SpellCheckerChanged([MarshalAs(UnmanagedType.Interface)] object handler);

            void remove_SpellCheckerChanged(uint eventCookie);

            [return: MarshalAs(UnmanagedType.Interface)]
            object GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId);

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumSpellingError ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text);
        }

        [ComImport]
        [Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumSpellingError
        {
            [PreserveSig]
            int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError? error);
        }

        [ComImport]
        [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISpellingError
        {
            uint get_StartIndex();

            uint get_Length();

            int get_CorrectiveAction();

            [return: MarshalAs(UnmanagedType.LPWStr)]
            string get_Replacement();
        }

        [ComImport]
        [Guid("00000101-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumString
        {
            [PreserveSig]
            int Next(int count, [MarshalAs(UnmanagedType.LPWStr)] out string? element, out int fetched);

            [PreserveSig]
            int Skip(int count);

            void Reset();

            [return: MarshalAs(UnmanagedType.Interface)]
            IEnumString Clone();
        }
    }
}
