using System;
using System.Collections.Generic;
using System.Linq;

namespace JustMd.Core.Spelling
{
    /// <summary>
    /// Checks mixed-script text: the languages of a script only see the letters of that script, and
    /// a word is an error only if all of them that can answer reject it.
    /// </summary>
    public sealed class DocumentSpeller
    {
        private readonly ISpellingEngine _engine;
        private readonly IReadOnlyDictionary<TextScript, IReadOnlyList<string>> _languages;

        public DocumentSpeller(ISpellingEngine engine, IReadOnlyDictionary<TextScript, IReadOnlyList<string>> languages)
        {
            _engine = engine;
            _languages = languages;
        }

        /// <summary>Whether any language checks this script. No script at all counts as no.</summary>
        public bool Checks(TextScript? script) => script is TextScript s && _languages.ContainsKey(s);

        /// <summary>
        /// The errors in the text, sorted by position. Each carries the first language of its script
        /// that could answer, with that language's action and replacement.
        /// </summary>
        public IReadOnlyList<SpellingError> Check(string text)
        {
            var errors = new List<SpellingError>();
            foreach ((TextScript script, IReadOnlyList<string> languages) in _languages)
            {
                string masked = Scripts.Mask(text, script);
                if (Scripts.HasLetter(masked))
                {
                    errors.AddRange(CheckScript(text, masked, languages));
                }
            }

            return errors.OrderBy(e => e.Start).ToList();
        }

        /// <summary>
        /// What the languages of the script suggest, the first of each, then the second of each and so
        /// on, so that a cut-off list still has some of every language. Nothing if the language is not in use.
        /// </summary>
        public IReadOnlyList<string> Suggest(string language, string word)
        {
            foreach (IReadOnlyList<string> languages in _languages.Values)
            {
                if (languages.Contains(language, StringComparer.Ordinal))
                {
                    List<IReadOnlyList<string>> answers = languages.Select(l => _engine.Suggest(l, word)).ToList();
                    int longest = answers.Max(a => a.Count);
                    return Enumerable.Range(0, longest)
                        .SelectMany(i => answers.Where(a => i < a.Count).Select(a => a[i]))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }
            }

            return Array.Empty<string>();
        }

        private List<SpellingError> CheckScript(string text, string masked, IReadOnlyList<string> languages)
        {
            List<SpellingError>? kept = null;
            foreach (string language in languages)
            {
                if (TryCheck(language, masked) is not IReadOnlyList<SpellingError> found)
                {
                    continue;
                }

                if (kept == null)
                {
                    kept = found
                        .Where(e => e.Start >= 0 && e.Length > 0 && e.Start <= text.Length - e.Length)
                        .Select(e => e with { Language = language })
                        .ToList();
                }
                else
                {
                    HashSet<(int, int)> hits = found.Select(e => (e.Start, e.Length)).ToHashSet();
                    kept.RemoveAll(e => !hits.Contains((e.Start, e.Length)));
                }

                if (kept.Count == 0)
                {
                    break;
                }
            }

            return kept ?? new List<SpellingError>();
        }

        /// <summary>Null if the language cannot answer: it has no say then, and the others still count.</summary>
        private IReadOnlyList<SpellingError>? TryCheck(string language, string masked)
        {
            try
            {
                return _engine.Check(language, masked);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
