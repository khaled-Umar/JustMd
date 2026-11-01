using System;
using System.Collections.Generic;

namespace JustMd.Core.Spelling
{
    /// <summary>Chooses the checker for each script from the user's Windows languages.</summary>
    public static class SpellingLanguages
    {
        /// <summary>
        /// Walks the list in order: the first usable language of a script wins, as the tag itself
        /// or else its primary subtag. A script with no usable language is missing from the result.
        /// </summary>
        public static IReadOnlyDictionary<TextScript, string> Assign(IEnumerable<string> userLanguages, Func<string, bool> isSupported)
        {
            var assigned = new Dictionary<TextScript, string>();
            foreach (string tag in userLanguages)
            {
                if (string.IsNullOrEmpty(tag) || Scripts.OfLanguage(tag) is not TextScript script || assigned.ContainsKey(script))
                {
                    continue;
                }

                if (isSupported(tag))
                {
                    assigned[script] = tag;
                    continue;
                }

                // The fallback must be in the same script: "sr-Latn" must not end up with "sr", which is Cyrillic.
                int dash = tag.IndexOf('-');
                if (dash > 0)
                {
                    string primary = tag.Substring(0, dash);
                    if (Scripts.OfLanguage(primary) == script && isSupported(primary))
                    {
                        assigned[script] = primary;
                    }
                }
            }

            return assigned;
        }
    }
}
