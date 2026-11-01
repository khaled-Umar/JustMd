using System;
using System.Linq;
using System.Text;
using JustMd.Core;
using JustMd.Core.Editing;

namespace JustMd.Tests
{
    public class DocumentTextTests
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        [Theory]
        [InlineData("# Title\n\nBody\n")]
        [InlineData("# Title\r\n\r\nBody\r\n")]
        [InlineData("---\ntitle: x\n---\n# Title\n")]
        [InlineData("---\r\ntitle: x\r\n---\r\n# Title\r\n")]
        [InlineData("---\ntitle: x\n---\n\n\n# Title\n")]
        [InlineData("---\r\ntitle: x\r\n---\r\n\r\n# Title\r\n")]
        [InlineData("<div dir=\"rtl\">\n\n# Title\n\n</div>\n")]
        [InlineData("<div dir=\"rtl\">\r\n\r\n# Title\r\n\r\n</div>\r\n")]
        [InlineData("---\ntitle: x\n---\n\n<div dir=\"rtl\">\n\n# Title\n\n</div>\n")]
        [InlineData("    indented code\n\ntext with hard break  \n")]
        [InlineData("---\ntitle: x\n---\n")]
        public void Parse_then_compose_is_identity(string text)
        {
            Assert.Equal(text, DocumentText.Parse(text, Utf8NoBom).Compose());
        }

        [Fact]
        public void Text_the_code_page_can_store_survives()
        {
            DocumentText document = DocumentText.Parse("# caf\u00e9\n", Windows1252());

            Assert.True(document.SurvivesEncoding());
        }

        [Theory]
        [InlineData("\u0645\u0631\u062d\u0628\u0627")] // Arabic: would be saved as "?????"
        [InlineData("\u0101")] // a with macron: best-fit mapping would silently save a plain "a"
        public void Text_the_code_page_cannot_store_does_not_survive(string typed)
        {
            DocumentText document = DocumentText.Parse("# Title\n", Windows1252()).With(typed, false);

            Assert.False(document.SurvivesEncoding());
        }

        [Fact]
        public void Falling_back_to_utf8_keeps_every_character_and_the_rest_of_the_document()
        {
            string original = "---\r\nt: x\r\n---\r\n\r\n# Title\r\n";
            DocumentText document = DocumentText.Parse(original, Windows1252()).With("# \u0645\u0631\u062d\u0628\u0627", true);

            DocumentText utf8 = document.AsUtf8();

            Assert.True(utf8.SurvivesEncoding());
            Assert.Equal(document.Compose(), utf8.Compose());
            Assert.Empty(utf8.Encoding.GetPreamble());
            Assert.Equal(utf8.Compose(), TextFileReader.Decode(utf8.ToBytes(), out Encoding detected));
            Assert.Equal(Encoding.UTF8.WebName, detected.WebName);
        }

        [Theory]
        [InlineData(false)] // the strict encoding TextFileReader uses for BOM-less UTF-8: throws
        [InlineData(true)] // its BOM variant: would quietly store U+FFFD
        public void A_lone_surrogate_survives_no_encoding(bool bom)
        {
            string body = "broken \ud83d here";
            DocumentText document = DocumentText.Parse("x\n", new UTF8Encoding(bom, throwOnInvalidBytes: !bom)).With(body, false);

            Assert.False(document.SurvivesEncoding());
            Assert.False(document.AsUtf8().SurvivesEncoding());
        }

        [Fact]
        public void A_valid_surrogate_pair_survives_utf8()
        {
            Assert.True(DocumentText.CreateEmpty().With("\ud83d\ude00", false).SurvivesEncoding());
        }

        private static Encoding Windows1252()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }

        [Theory]
        [InlineData("utf-8", true)]
        [InlineData("utf-8", false)]
        [InlineData("utf-16", true)]
        public void Bytes_round_trip_through_the_reader(string encodingName, bool bom)
        {
            Encoding encoding = encodingName == "utf-16"
                ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
                : new UTF8Encoding(bom);
            string text = "---\r\nt: x\r\n---\r\n\r\n# مرحبا\r\n\r\nbody\r\n";
            byte[] original = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

            string decoded = TextFileReader.Decode(original, out Encoding detected);

            Assert.Equal(original, DocumentText.Parse(decoded, detected).ToBytes());
        }

        [Fact]
        public void Body_is_lf_only_without_front_matter_wrapper_or_outer_blank_lines()
        {
            DocumentText document = DocumentText.Parse(
                "---\r\nt: x\r\n---\r\n\r\n<div dir='rtl'>\r\n\r\n\r\n# A\r\n\r\nB\r\n\r\n</div>\r\n\r\n", Utf8NoBom);

            Assert.Equal("---\nt: x\n---\n", document.FrontMatter);
            Assert.Equal("# A\n\nB", document.Body);
            Assert.True(document.IsRtl);
            Assert.Equal("\r\n", document.NewLine);
        }

        [Fact]
        public void Outer_blank_lines_go_but_indentation_of_the_first_line_stays()
        {
            Assert.Equal("    code", DocumentText.Parse("\n  \n    code\n\n \n", Utf8NoBom).Body);
        }

        [Theory]
        [InlineData("a\r\nb\r\nc\n", "\r\n")]
        [InlineData("a\nb\nc\r\n", "\n")]
        [InlineData("a\r\nb\n", "\n")]
        [InlineData("no newline", "\n")]
        public void New_line_follows_the_majority(string text, string expected)
        {
            DocumentText document = DocumentText.Parse(text, Utf8NoBom);

            Assert.Equal(expected, document.NewLine);
            Assert.DoesNotContain('\r', document.Body);
        }

        [Fact]
        public void Mixed_newlines_are_unified_on_compose()
        {
            Assert.Equal("a\r\nb\r\nc\r\n", DocumentText.Parse("a\r\nb\r\nc\n", Utf8NoBom).Compose());
        }

        [Theory]
        [InlineData("a", "a\n")]
        [InlineData("a\n", "a\n")]
        [InlineData("a\n\n\n", "a\n")]
        public void Compose_ends_with_exactly_one_newline(string text, string expected)
        {
            Assert.Equal(expected, DocumentText.Parse(text, Utf8NoBom).Compose());
        }

        [Fact]
        public void Empty_document_is_crlf_utf8_without_bom()
        {
            DocumentText document = DocumentText.CreateEmpty();

            Assert.Equal(string.Empty, document.FrontMatter);
            Assert.Equal(string.Empty, document.Body);
            Assert.False(document.IsRtl);
            Assert.Equal("\r\n", document.NewLine);
            Assert.Empty(document.Encoding.GetPreamble());
            Assert.Equal("\r\n", document.Compose());
            Assert.Equal("# T\r\n", document.With("# T", false).Compose());
        }

        [Fact]
        public void With_keeps_everything_but_the_body()
        {
            var bom = new UTF8Encoding(true);
            DocumentText document = DocumentText.Parse("---\r\nt: x\r\n---\r\n\r\nold\r\n", bom);

            DocumentText edited = document.With("new\n\nlines\n\n", false);

            Assert.Equal("---\r\nt: x\r\n---\r\n\r\nnew\r\n\r\nlines\r\n", edited.Compose());
            Assert.Same(bom, edited.Encoding);
            Assert.Equal("old", document.Body);
        }

        [Fact]
        public void Emptying_the_body_leaves_only_the_front_matter()
        {
            DocumentText document = DocumentText.Parse("---\nt: x\n---\n\nold\n", Utf8NoBom);

            Assert.Equal("---\nt: x\n---\n", document.With(string.Empty, false).Compose());
        }

        [Fact]
        public void Toggling_rtl_adds_and_removes_the_wrapper()
        {
            DocumentText document = DocumentText.Parse("# T\n", Utf8NoBom);

            DocumentText rtl = document.With(document.Body, true);
            Assert.Equal("<div dir=\"rtl\">\n\n# T\n\n</div>\n", rtl.Compose());

            DocumentText reparsed = DocumentText.Parse(rtl.Compose(), Utf8NoBom);
            Assert.True(reparsed.IsRtl);
            Assert.Equal("# T\n", reparsed.With(reparsed.Body, false).Compose());
        }

        [Fact]
        public void Non_canonical_wrapper_is_recognised_and_normalised()
        {
            DocumentText document = DocumentText.Parse("<DIV DIR=RTL>\n# T\n</DIV>\n", Utf8NoBom);

            Assert.True(document.IsRtl);
            Assert.Equal("# T", document.Body);
            Assert.Equal("<div dir=\"rtl\">\n\n# T\n\n</div>\n", document.Compose());
        }

        [Fact]
        public void Bytes_start_with_the_encoding_preamble()
        {
            byte[] bytes = DocumentText.Parse("a", new UTF8Encoding(true)).ToBytes();

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' }, bytes);
        }
    }

    public class FrontMatterTests
    {
        [Theory]
        [InlineData("---\nt: x\n---\nBody\n", "---\nt: x\n---\n", "Body\n")]
        [InlineData("---\nt: x\n...\nBody\n", "---\nt: x\n...\n", "Body\n")]
        [InlineData("---\nt: x\n---   \n\nBody\n", "---\nt: x\n---   \n", "\nBody\n")]
        [InlineData("---\nt: x\n---", "---\nt: x\n---\n", "")]
        [InlineData("---\r\nt: x\r\n---\r\nBody\r\n", "---\r\nt: x\r\n---\r\n", "Body\r\n")]
        public void Splits_a_real_front_matter_block(string text, string frontMatter, string body)
        {
            Assert.Equal((frontMatter, body), FrontMatter.Split(text));
        }

        [Theory]
        [InlineData("# Title\n")]
        [InlineData("")]
        [InlineData("---\nnot closed\n")]
        [InlineData("\n---\nt: x\n---\n")]
        [InlineData("----\nt: x\n----\n")]
        public void Text_without_front_matter_is_returned_whole(string text)
        {
            Assert.Equal((string.Empty, text), FrontMatter.Split(text));
        }

        [Theory]
        [InlineData("---\nSecret\n---\nVisible\n")]
        [InlineData("---\nSecret\n...\nVisible\n")]
        [InlineData("---\n\nSecret\n\n---\nVisible\n")]
        [InlineData("---\nSecret\n---")]
        public void Hides_exactly_what_the_viewer_hides(string text)
        {
            var renderer = new MarkdownRenderer();

            Assert.DoesNotContain("Secret", renderer.RenderHtml(text, null));
            Assert.Contains("Secret", FrontMatter.Split(text).FrontMatter);
            Assert.Equal(renderer.RenderHtml(text, null), renderer.RenderHtml(FrontMatter.Split(text).Body, null));
        }
    }

    public class RtlWrapperTests
    {
        [Theory]
        [InlineData("<div dir=\"rtl\">\n\nx\n\n</div>")]
        [InlineData("<div dir='rtl'>\n\nx\n\n</div>")]
        [InlineData("<div dir=rtl>\n\nx\n\n</div>")]
        [InlineData("<DIV DIR=\"RTL\">\n\nx\n\n</DIV>")]
        [InlineData("<div dir=\"rtl\">\nx\n</div>")]
        [InlineData("\n\n  <div  dir = \"rtl\" >  \n\nx\n\n  </div >  \n\n")]
        [InlineData("<div dir=\"rtl\">\r\n\r\nx\r\n\r\n</div>\r\n")]
        public void Accepts_wrapper_forms(string body)
        {
            Assert.True(RtlWrapper.TryUnwrap(body, out string inner));
            Assert.Equal("x", inner);
        }

        [Theory]
        [InlineData("<div dir=\"rtl\" align=\"right\">\n\nx\n\n</div>")]
        [InlineData("<div align=\"right\" dir=\"rtl\">\n\nx\n\n</div>")]
        [InlineData("<div dir=\"ltr\">\n\nx\n\n</div>")]
        [InlineData("<div>\n\nx\n\n</div>")]
        [InlineData("<div dir=\"rtl\">\n\nx\n")]
        [InlineData("<div dir=\"rtl\">\n\nx\n\n</div>\n\nafter")]
        [InlineData("before\n\n<div dir=\"rtl\">\n\nx\n\n</div>")]
        [InlineData("<div dir=\"rtl\">x</div>")]
        [InlineData("    <div dir=\"rtl\">\n\nx\n\n</div>")]
        [InlineData("<div dir=\"rtl\">\n\na\n\n</div>\n\nb\n\n<div>\n\nc\n\n</div>")]
        [InlineData("<div dir=\"rtl\">\n\n<div>\n\na\n\n</div>")]
        [InlineData("")]
        public void Rejects_what_is_not_a_wrapper(string body)
        {
            Assert.False(RtlWrapper.TryUnwrap(body, out _));
        }

        [Fact]
        public void Nested_divs_stay_in_the_body()
        {
            string nested = "<div>\n\na\n\n</div>";

            Assert.True(RtlWrapper.TryUnwrap(RtlWrapper.Wrap(nested), out string inner));
            Assert.Equal(nested, inner);
        }

        [Fact]
        public void Div_tags_inside_code_do_not_count()
        {
            string code = "```html\n</div>\n<div>\n</div>\n```";

            Assert.True(RtlWrapper.TryUnwrap(RtlWrapper.Wrap(code), out string inner));
            Assert.Equal(code, inner);
        }

        [Fact]
        public void Wrap_uses_the_canonical_form()
        {
            Assert.Equal("<div dir=\"rtl\">\n\nx\n\n</div>", RtlWrapper.Wrap("x"));
        }
    }
}
