using System.Collections.Generic;
using System.Linq;

namespace JustMd.Core.Spelling
{
    /// <summary>Checks mixed-script text: each assigned language only sees the letters of its own script.</summary>
    public sealed class DocumentSpeller
    {
        private readonly ISpellingEngine _engine;
        private readonly IReadOnlyDictionary<TextScript, string> _languages;

        public DocumentSpeller(ISpellingEngine engine, IReadOnlyDictionary<TextScript, string> languages)
        {
            _engine = engine;
            _languages = languages;
        }

        /// <summary>The errors in the text, tagged with their language and sorted by position.</summary>
        public IReadOnlyList<SpellingError> Check(string text)
        {
            var errors = new List<SpellingError>();
            foreach ((TextScript script, string language) in _languages)
            {
                string masked = Scripts.Mask(text, script);
                if (!Scripts.HasLetter(masked))
                {
                    continue;
                }

                foreach (SpellingError error in _engine.Check(language, masked))
                {
                    if (error.Start >= 0 && error.Length > 0 && error.Start <= text.Length - error.Length)
                    {
                        errors.Add(error with { Language = language });
                    }
                }
            }

            return errors.OrderBy(e => e.Start).ToList();
        }
    }
}
