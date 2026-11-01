using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JustMd.Core.Spelling
{
    /// <summary>Which script a character or a language tag belongs to, and hiding the other scripts from a checker.</summary>
    public static class Scripts
    {
        private static readonly HashSet<string> ArabicLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "ar", "fa", "ur", "ps", "ckb", "sd", "ug",
        };

        // Written in a script JustMd does not check, so no text would ever reach their checkers.
        private static readonly HashSet<string> OtherScriptLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "he", "yi", "ru", "uk", "be", "bg", "mk", "sr", "kk", "ky", "mn", "tg", "el", "hy", "ka", "am", "ti",
            "zh", "ja", "ko", "th", "lo", "km", "my", "hi", "mr", "ne", "bn", "as", "pa", "gu", "or", "ta", "te",
            "kn", "ml", "si", "dv", "bo",
        };

        public static TextScript OfRune(Rune rune)
        {
            int code = rune.Value;
            if (code is (>= 0x0600 and <= 0x06FF) or (>= 0x0750 and <= 0x077F) or (>= 0x08A0 and <= 0x08FF)
                or (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) or (>= 0x1EE00 and <= 0x1EEFF))
            {
                return TextScript.Arabic;
            }

            return Rune.IsLetter(rune) && (code < 0x0250 || code is >= 0x1E00 and <= 0x1EFF)
                ? TextScript.Latin
                : TextScript.None;
        }

        /// <summary>The script a language tag is written in, or null if JustMd never uses that language.</summary>
        public static TextScript? OfLanguage(string tag)
        {
            string[] subtags = tag.Split('-');
            if (subtags[0].Length == 0)
            {
                return null;
            }

            // A one-letter subtag starts an extension, which holds no script.
            for (int i = 1; i < subtags.Length && subtags[i].Length > 1; i++)
            {
                if (subtags[i].Length == 4 && IsAsciiLetters(subtags[i]))
                {
                    // Any other script subtag names a script JustMd does not check (uz-Cyrl).
                    return subtags[i].ToLowerInvariant() switch
                    {
                        "arab" => TextScript.Arabic,
                        "latn" => TextScript.Latin,
                        _ => null,
                    };
                }
            }

            if (ArabicLanguages.Contains(subtags[0]))
            {
                return TextScript.Arabic;
            }

            return OtherScriptLanguages.Contains(subtags[0]) ? null : TextScript.Latin;
        }

        /// <summary>
        /// Replaces the letters of every other script, and the marks on them, with one space per
        /// UTF-16 unit so offsets keep their meaning. Digits, punctuation and whitespace stay.
        /// </summary>
        public static string Mask(string text, TextScript script)
        {
            var masked = new StringBuilder(text);
            bool afterLetter = false;
            TextScript previous = TextScript.None;
            int index = 0;
            while (index < text.Length)
            {
                if (Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done)
                {
                    afterLetter = false;
                    index += consumed;
                    continue;
                }

                bool isLetter = Rune.IsLetter(rune);
                bool isMark = IsMark(rune);
                TextScript current = isMark && afterLetter ? previous : OfRune(rune);

                // A mark with nothing to attach to and no script of its own, such as the
                // variation selector after an emoji, is not text in another script.
                if (current != script && (isLetter || (isMark && (afterLetter || current != TextScript.None))))
                {
                    for (int i = 0; i < consumed; i++)
                    {
                        masked[index + i] = ' ';
                    }
                }

                afterLetter = isLetter || (isMark && afterLetter);
                previous = current;
                index += consumed;
            }

            return masked.ToString();
        }

        public static bool HasLetter(string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (Rune.IsLetter(rune))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMark(Rune rune) =>
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;

        private static bool IsAsciiLetters(string text)
        {
            foreach (char c in text)
            {
                if (!char.IsAsciiLetter(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
