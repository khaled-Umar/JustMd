using System;
using System.Collections.Generic;
using System.Linq;

namespace JustMd.Core.Spelling
{
    /// <summary>Chooses the checkers for each script from the user's Windows languages.</summary>
    public static class SpellingLanguages
    {
        /// <summary>
        /// Walks the list in order and collects the usable languages of each script, each as the tag
        /// itself or else its primary subtag, without repeats. A script with no usable language is
        /// missing from the result.
        /// </summary>
        public static IReadOnlyDictionary<TextScript, IReadOnlyList<string>> Assign(IEnumerable<string> userLanguages, Func<string, bool> isSupported)
        {
            var assigned = new Dictionary<TextScript, List<string>>();
            foreach (string tag in userLanguages)
            {
                if (string.IsNullOrEmpty(tag) || Scripts.OfLanguage(tag) is not TextScript script
                    || Usable(tag, script, isSupported) is not string language)
                {
                    continue;
                }

                if (!assigned.TryGetValue(script, out List<string>? languages))
                {
                    languages = new List<string>();
                    assigned[script] = languages;
                }

                if (!languages.Contains(language, StringComparer.Ordinal))
                {
                    languages.Add(language);
                }
            }

            return assigned.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
        }

        private static string? Usable(string tag, TextScript script, Func<string, bool> isSupported)
        {
            if (isSupported(tag))
            {
                return tag;
            }

            // The fallback must be in the same script: "sr-Latn" must not end up with "sr", which is Cyrillic.
            int dash = tag.IndexOf('-');
            if (dash > 0)
            {
                string primary = tag.Substring(0, dash);
                if (Scripts.OfLanguage(primary) == script && isSupported(primary))
                {
                    return primary;
                }
            }

            return null;
        }
    }
}
