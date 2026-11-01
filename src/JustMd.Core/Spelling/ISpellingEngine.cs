using System;
using System.Collections.Generic;

namespace JustMd.Core.Spelling
{
    /// <summary>A spell checker that works per language.</summary>
    public interface ISpellingEngine : IDisposable
    {
        bool IsSupported(string language);

        /// <summary>The misspellings in the text. Throws if the language cannot answer: an empty list means every word is accepted.</summary>
        IReadOnlyList<SpellingError> Check(string language, string text);

        IReadOnlyList<string> Suggest(string language, string word);

        /// <summary>Stops flagging the word until the process ends.</summary>
        void Ignore(string language, string word);

        /// <summary>Adds the word to the user's dictionary for this language.</summary>
        void Add(string language, string word);
    }
}
