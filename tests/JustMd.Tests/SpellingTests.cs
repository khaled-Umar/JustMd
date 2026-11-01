using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JustMd.Core.Spelling;

namespace JustMd.Tests
{
    public class SpellingTests
    {
        private const string Tashkeel = "مُحَمَّد"; // Muhammad, fully vowelled
        private const string Emoji = "\U0001F600";

        private static readonly IReadOnlyDictionary<TextScript, string> Both = new Dictionary<TextScript, string>
        {
            [TextScript.Latin] = "en-US",
            [TextScript.Arabic] = "ar-SA",
        };

        // ---------- scripts ----------

        [Theory]
        [InlineData('a', TextScript.Latin)]
        [InlineData('é', TextScript.Latin)]
        [InlineData('ẞ', TextScript.Latin)]
        [InlineData('م', TextScript.Arabic)]
        [InlineData('ݐ', TextScript.Arabic)]
        [InlineData('ﻻ', TextScript.Arabic)]
        [InlineData('1', TextScript.None)]
        [InlineData('.', TextScript.None)]
        [InlineData('א', TextScript.None)]
        [InlineData('Ж', TextScript.None)]
        public void Runes_have_a_script(char c, TextScript expected)
        {
            Assert.Equal(expected, Scripts.OfRune(new Rune(c)));
        }

        [Fact]
        public void Supplementary_arabic_letters_are_arabic_and_emoji_are_nothing()
        {
            Assert.Equal(TextScript.Arabic, Scripts.OfRune(new Rune(0x1EE00)));
            Assert.Equal(TextScript.None, Scripts.OfRune(new Rune(0x1F600)));
        }

        [Theory]
        [InlineData("en-US", TextScript.Latin)]
        [InlineData("de", TextScript.Latin)]
        [InlineData("ar-SA", TextScript.Arabic)]
        [InlineData("fa-IR", TextScript.Arabic)]
        [InlineData("ckb", TextScript.Arabic)]
        [InlineData("sr-Latn", TextScript.Latin)]
        [InlineData("sr-Latn-RS", TextScript.Latin)]
        [InlineData("ha-Arab", TextScript.Arabic)]
        [InlineData("AR-sa", TextScript.Arabic)]
        [InlineData("ca-ES-valencia", TextScript.Latin)]
        [InlineData("sr", null)]
        [InlineData("sr-Cyrl", null)]
        [InlineData("uz-Cyrl", null)]
        [InlineData("zh-Hans-CN", null)]
        [InlineData("ru-RU", null)]
        [InlineData("he", null)]
        [InlineData("", null)]
        public void Languages_have_a_script(string tag, TextScript? expected)
        {
            Assert.Equal(expected, Scripts.OfLanguage(tag));
        }

        // ---------- masking ----------

        [Theory]
        [InlineData(TextScript.Latin)]
        [InlineData(TextScript.Arabic)]
        public void Mask_preserves_length(TextScript script)
        {
            string text = "hello " + Tashkeel + " " + Emoji + "️ \U0001EE00 café א.";

            Assert.Equal(text.Length, Scripts.Mask(text, script).Length);
        }

        [Fact]
        public void Latin_survives_the_arabic_mask_as_spaces_and_the_reverse()
        {
            const string arabic = "محمد";

            Assert.Equal("abc     ", Scripts.Mask("abc " + arabic, TextScript.Latin));
            Assert.Equal("    " + arabic, Scripts.Mask("abc " + arabic, TextScript.Arabic));
        }

        [Fact]
        public void Mask_blanks_tashkeel_with_its_letters()
        {
            Assert.Equal(new string(' ', Tashkeel.Length), Scripts.Mask(Tashkeel, TextScript.Latin));
            Assert.Equal(Tashkeel, Scripts.Mask(Tashkeel, TextScript.Arabic));
        }

        [Fact]
        public void A_combining_mark_follows_the_letter_before_it()
        {
            const string decomposed = "é";

            Assert.Equal(decomposed, Scripts.Mask(decomposed, TextScript.Latin));
            Assert.Equal("  ", Scripts.Mask(decomposed, TextScript.Arabic));
        }

        [Fact]
        public void Letters_of_other_scripts_are_blanked_for_every_language()
        {
            Assert.Equal("  ", Scripts.Mask("אב", TextScript.Latin));
            Assert.Equal("  ", Scripts.Mask("אב", TextScript.Arabic));
        }

        [Theory]
        [InlineData(TextScript.Latin)]
        [InlineData(TextScript.Arabic)]
        public void Digits_punctuation_whitespace_and_emoji_stay(TextScript script)
        {
            string text = "12 \t.,!\n" + Emoji + "️";

            Assert.Equal(text, Scripts.Mask(text, script));
        }

        [Fact]
        public void A_lone_surrogate_does_not_break_the_mask()
        {
            Assert.Equal("a\uD83D ", Scripts.Mask("a\uD83Dم", TextScript.Latin));
        }

        // ---------- assigning languages ----------

        [Fact]
        public void Assign_finds_one_language_per_script()
        {
            IReadOnlyDictionary<TextScript, string> assigned =
                SpellingLanguages.Assign(new[] { "en-US", "ar-SA" }, _ => true);

            Assert.Equal(2, assigned.Count);
            Assert.Equal("en-US", assigned[TextScript.Latin]);
            Assert.Equal("ar-SA", assigned[TextScript.Arabic]);
        }

        [Fact]
        public void Assign_leaves_a_script_without_a_language_out()
        {
            IReadOnlyDictionary<TextScript, string> assigned = SpellingLanguages.Assign(new[] { "ar-SA" }, _ => true);

            Assert.Equal("ar-SA", Assert.Single(assigned).Value);
        }

        [Fact]
        public void Assign_falls_back_to_the_primary_subtag()
        {
            IReadOnlyDictionary<TextScript, string> assigned = SpellingLanguages.Assign(new[] { "en-GB" }, tag => tag == "en");

            Assert.Equal("en", assigned[TextScript.Latin]);
        }

        [Fact]
        public void Assign_prefers_the_tag_over_its_primary_subtag()
        {
            IReadOnlyDictionary<TextScript, string> assigned = SpellingLanguages.Assign(new[] { "en-GB" }, _ => true);

            Assert.Equal("en-GB", assigned[TextScript.Latin]);
        }

        [Fact]
        public void Assign_takes_the_script_from_the_subtag()
        {
            IReadOnlyDictionary<TextScript, string> assigned = SpellingLanguages.Assign(new[] { "sr-Latn" }, _ => true);

            Assert.Equal("sr-Latn", assigned[TextScript.Latin]);
        }

        [Fact]
        public void Assign_does_not_fall_back_to_a_primary_subtag_of_another_script()
        {
            Assert.Empty(SpellingLanguages.Assign(new[] { "sr-Latn" }, tag => tag == "sr"));
            Assert.Empty(SpellingLanguages.Assign(new[] { "ar-Latn" }, tag => tag == "ar"));
        }

        [Fact]
        public void Assign_skips_unsupported_languages_and_takes_the_next_of_that_script()
        {
            IReadOnlyDictionary<TextScript, string> assigned =
                SpellingLanguages.Assign(new[] { "xx-XX", "de-DE", "en-US" }, tag => tag is "de-DE" or "en-US");

            Assert.Equal("de-DE", assigned[TextScript.Latin]);
        }

        [Fact]
        public void Assign_ignores_languages_of_unchecked_scripts_even_if_supported()
        {
            Assert.Empty(SpellingLanguages.Assign(new[] { "ru-RU", "he", "zh-CN", "" }, _ => true));
        }

        [Fact]
        public void Assign_with_nothing_supported_is_empty()
        {
            Assert.Empty(SpellingLanguages.Assign(new[] { "en-US", "ar-SA" }, _ => false));
        }

        // ---------- checking ----------

        [Fact]
        public void Each_language_sees_only_its_own_script()
        {
            const string arabic = "محمد";
            var engine = new FakeEngine();

            new DocumentSpeller(engine, Both).Check("abc " + arabic);

            Assert.Contains(("en-US", "abc     "), engine.Checked);
            Assert.Contains(("ar-SA", "    " + arabic), engine.Checked);
        }

        [Theory]
        [InlineData("abc", "en-US")]
        [InlineData("محمد", "ar-SA")]
        public void A_language_with_nothing_to_check_is_never_called(string text, string only)
        {
            var engine = new FakeEngine();

            new DocumentSpeller(engine, Both).Check(text);

            Assert.Equal(only, Assert.Single(engine.Checked).Language);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("123 .,!")]
        [InlineData("אב")]
        [InlineData(Emoji)]
        public void Text_with_no_letter_of_an_assigned_script_never_reaches_the_engine(string text)
        {
            var engine = new FakeEngine();

            Assert.Empty(new DocumentSpeller(engine, Both).Check(text));
            Assert.Empty(engine.Checked);
        }

        [Fact]
        public void Unassigned_scripts_are_not_checked()
        {
            const string arabic = "محمد";
            var engine = new FakeEngine();
            var latinOnly = new Dictionary<TextScript, string> { [TextScript.Latin] = "en-US" };

            new DocumentSpeller(engine, latinOnly).Check(arabic);

            Assert.Empty(engine.Checked);
        }

        [Fact]
        public void Errors_are_tagged_with_their_language_and_sorted()
        {
            const string arabic = "محمد";
            var engine = new FakeEngine
            {
                Answers =
                {
                    ["en-US"] = new[] { new SpellingError(5, 3, 1, null) },
                    ["ar-SA"] = new[] { new SpellingError(0, 4, 2, "x") },
                },
            };

            IReadOnlyList<SpellingError> errors = new DocumentSpeller(engine, Both).Check(arabic + " abc");

            Assert.Equal(new[] { 0, 5 }, errors.Select(e => e.Start));
            Assert.Equal(new[] { "ar-SA", "en-US" }, errors.Select(e => e.Language));
            Assert.Equal("x", errors[0].Replacement);
        }

        [Theory]
        [InlineData(-1, 2)]
        [InlineData(0, 0)]
        [InlineData(0, -1)]
        [InlineData(0, 4)]
        [InlineData(3, 1)]
        [InlineData(4, 1)]
        [InlineData(1, int.MaxValue)]
        [InlineData(int.MaxValue, int.MaxValue)]
        public void Bogus_offsets_are_dropped(int start, int length)
        {
            var engine = new FakeEngine { Answers = { ["en-US"] = new[] { new SpellingError(start, length, 1, null) } } };

            Assert.Empty(new DocumentSpeller(engine, Both).Check("abc"));
        }

        [Fact]
        public void An_error_up_to_the_end_of_the_text_is_kept()
        {
            var engine = new FakeEngine { Answers = { ["en-US"] = new[] { new SpellingError(0, 3, 1, null) } } };

            Assert.Single(new DocumentSpeller(engine, Both).Check("abc"));
        }

        // ---------- the worker's queue ----------

        [Fact]
        public void Urgent_work_goes_first_and_each_queue_is_in_order()
        {
            var queue = new SpellingWorkQueue<string>();
            queue.Enqueue(1, urgent: false, "scan 1");
            queue.Enqueue(1, urgent: true, "typed 1");
            queue.Enqueue(1, urgent: false, "scan 2");
            queue.Enqueue(2, urgent: true, "typed 2");

            Assert.Equal(new[] { "typed 1", "typed 2", "scan 1", "scan 2" }, Drain(queue));
        }

        [Fact]
        public void Forget_drops_the_queued_work_of_that_tab_only()
        {
            var queue = new SpellingWorkQueue<string>();
            queue.Enqueue(1, urgent: false, "scan of 1");
            queue.Enqueue(2, urgent: false, "scan of 2");
            queue.Enqueue(1, urgent: true, "typed in 1");

            queue.Forget(1);

            Assert.Equal(new[] { "scan of 2" }, Drain(queue));
        }

        [Fact]
        public void Forget_marks_running_work_stale_but_not_work_queued_afterwards()
        {
            var queue = new SpellingWorkQueue<string>();
            queue.Enqueue(1, urgent: false, "old");
            queue.Enqueue(2, urgent: false, "other tab");
            Assert.True(queue.TryDequeue(out SpellingWorkQueue<string>.Item running));
            Assert.True(queue.IsCurrent(running));

            queue.Forget(1);
            queue.Enqueue(1, urgent: true, "new");

            Assert.False(queue.IsCurrent(running));
            Assert.True(queue.TryDequeue(out SpellingWorkQueue<string>.Item next));
            Assert.Equal("new", next.Work);
            Assert.True(queue.IsCurrent(next));
            Assert.True(queue.TryDequeue(out SpellingWorkQueue<string>.Item other));
            Assert.True(queue.IsCurrent(other));
        }

        [Fact]
        public void Work_with_no_tab_survives_forgetting_any_tab()
        {
            var queue = new SpellingWorkQueue<string>();
            queue.Enqueue(SpellingWorkQueue<string>.NoTab, urgent: true, "add word");

            queue.Forget(1);

            Assert.Equal(new[] { "add word" }, Drain(queue));
        }

        private static List<string> Drain(SpellingWorkQueue<string> queue)
        {
            var work = new List<string>();
            while (queue.TryDequeue(out SpellingWorkQueue<string>.Item item))
            {
                work.Add(item.Work);
            }

            return work;
        }

        private sealed class FakeEngine : ISpellingEngine
        {
            public List<(string Language, string Text)> Checked { get; } = new();

            public Dictionary<string, SpellingError[]> Answers { get; } = new();

            public bool IsSupported(string language) => true;

            public IReadOnlyList<SpellingError> Check(string language, string text)
            {
                Checked.Add((language, text));
                return Answers.TryGetValue(language, out SpellingError[]? errors) ? errors : Array.Empty<SpellingError>();
            }

            public IReadOnlyList<string> Suggest(string language, string word) => Array.Empty<string>();

            public void Ignore(string language, string word)
            {
            }

            public void Add(string language, string word)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
