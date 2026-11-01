using System;
using System.Text;

namespace JustMd.Core.Editing
{
    /// <summary>
    /// A Markdown file split into what the editor sees (<see cref="Body"/>, <see cref="IsRtl"/>)
    /// and what it never sees but saving must keep: front matter, line endings and encoding.
    /// </summary>
    public sealed class DocumentText
    {
        /// <summary>Blank lines before the content (usually after front matter); kept on save.</summary>
        private readonly string _leadingBlankLines;

        /// <summary>The text as read, raw endings: unchanged lines take their ending from it.</summary>
        private readonly string _original;

        /// <summary>Newlines after the content in the LF-only original; kept on save.</summary>
        private readonly int _trailingNewLines;

        private DocumentText(
            string frontMatter, string leadingBlankLines, string body, bool isRtl, string newLine, Encoding encoding, string original, int trailingNewLines)
        {
            FrontMatter = frontMatter;
            _leadingBlankLines = leadingBlankLines;
            Body = body;
            IsRtl = isRtl;
            NewLine = newLine;
            Encoding = encoding;
            _original = original;
            _trailingNewLines = trailingNewLines;
        }

        /// <summary>Empty, or the YAML block including its closing line and one "\n" (LF-only).</summary>
        public string FrontMatter { get; }

        /// <summary>LF-only, RTL wrapper removed, no leading or trailing blank lines.</summary>
        public string Body { get; }

        public bool IsRtl { get; }

        /// <summary>"\r\n" if the original had more CRLF than LF, else "\n": the ending of lines the original does not have.</summary>
        public string NewLine { get; }

        /// <summary>As returned by <see cref="TextFileReader"/>; its preamble decides the BOM.</summary>
        public Encoding Encoding { get; }

        public static DocumentText Parse(string text, Encoding encoding)
        {
            string newLine = DetectNewLine(text);
            (string frontMatter, string content) = Editing.FrontMatter.Split(ToLf(text));
            string trimmed = TrimBlankLines(content, out string leading);
            bool isRtl = RtlWrapper.TryUnwrap(trimmed, out string inner);

            // A file without content has no trailing newlines to keep: what gets typed into it ends like a new file.
            int trailingNewLines = trimmed.Length == 0 ? 1 : CountNewLines(content, leading.Length + trimmed.Length);
            return new DocumentText(frontMatter, leading, isRtl ? inner : trimmed, isRtl, newLine, encoding, text, trailingNewLines);
        }

        public static DocumentText CreateEmpty() =>
            new(string.Empty, string.Empty, string.Empty, false, "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), string.Empty, 1);

        public DocumentText With(string body, bool isRtl) =>
            new(FrontMatter, _leadingBlankLines, TrimBlankLines(ToLf(body), out _), isRtl, NewLine, Encoding, _original, _trailingNewLines);

        /// <summary>The same document, to be stored as UTF-8 without a BOM.</summary>
        public DocumentText AsUtf8() =>
            new(FrontMatter, _leadingBlankLines, Body, IsRtl, NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), _original, _trailingNewLines);

        /// <summary>
        /// False if the next read of the stored file would not return <see cref="Compose"/>: a legacy
        /// code page turns what it lacks into "?" or a look-alike, a lone surrogate fits no encoding,
        /// and legacy bytes that happen to be valid UTF-8 get read as UTF-8.
        /// </summary>
        public bool SurvivesEncoding()
        {
            string text = Compose();
            try
            {
                return string.Equals(TextFileReader.Decode(ToBytes(), out _), text, StringComparison.Ordinal);
            }
            catch (ArgumentException)
            {
                // EncoderFallbackException and DecoderFallbackException, from a strict encoding.
                return false;
            }
        }

        /// <summary>
        /// The full file text: front matter, then the body (wrapped if RTL), the original's trailing
        /// newlines. Unchanged lines keep their ending, the others get <see cref="NewLine"/>.
        /// Without a preamble, a U+FEFF at the very start is dropped: the next read would take it
        /// for a BOM, so it could not be kept anyway.
        /// </summary>
        public string Compose()
        {
            string text = LineEndings.Apply(ComposeLf(), _original, NewLine);
            return Encoding.GetPreamble().Length == 0 ? text.TrimStart('\uFEFF') : text;
        }

        /// <summary>What <see cref="Compose"/> returns, before line endings: LF-only.</summary>
        internal string ComposeLf()
        {
            string content = IsRtl ? RtlWrapper.Wrap(Body) : Body;
            string text = FrontMatter + (content.Length == 0 ? string.Empty : _leadingBlankLines + content + new string('\n', _trailingNewLines));
            return text.Length == 0 ? "\n" : text;
        }

        /// <summary>The bytes to write: BOM (if the encoding has one) followed by <see cref="Compose"/>.</summary>
        public byte[] ToBytes()
        {
            byte[] preamble = Encoding.GetPreamble();
            byte[] content = Encoding.GetBytes(Compose());
            byte[] bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);
            return bytes;
        }

        internal static string ToLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>
        /// Removes whole blank lines from both ends of LF-only text. Spaces on lines that have
        /// content are kept: they may mean an indented code block or a hard line break.
        /// </summary>
        internal static string TrimBlankLines(string text, out string leading)
        {
            leading = string.Empty;
            int start = 0;
            int first = 0;
            while (first < text.Length && text[first] is ' ' or '\t' or '\n')
            {
                if (text[first] == '\n')
                {
                    start = first + 1;
                }

                first++;
            }

            if (first == text.Length)
            {
                return string.Empty;
            }

            int last = text.Length - 1;
            while (text[last] is ' ' or '\t' or '\n')
            {
                last--;
            }

            int end = text.IndexOf('\n', last);
            leading = text.Substring(0, start);
            return text.Substring(start, (end < 0 ? text.Length : end) - start);
        }

        private static int CountNewLines(string text, int start)
        {
            int count = 0;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static string DetectNewLine(string text)
        {
            int crlf = 0;
            int lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (i > 0 && text[i - 1] == '\r')
                    {
                        crlf++;
                    }
                    else
                    {
                        lf++;
                    }
                }
            }

            return crlf > lf ? "\r\n" : "\n";
        }
    }
}
