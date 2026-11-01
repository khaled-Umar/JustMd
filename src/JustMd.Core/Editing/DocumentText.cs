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

        private DocumentText(string frontMatter, string leadingBlankLines, string body, bool isRtl, string newLine, Encoding encoding)
        {
            FrontMatter = frontMatter;
            _leadingBlankLines = leadingBlankLines;
            Body = body;
            IsRtl = isRtl;
            NewLine = newLine;
            Encoding = encoding;
        }

        /// <summary>Empty, or the YAML block including its closing line and one "\n" (LF-only).</summary>
        public string FrontMatter { get; }

        /// <summary>LF-only, RTL wrapper removed, no leading or trailing blank lines.</summary>
        public string Body { get; }

        public bool IsRtl { get; }

        /// <summary>"\r\n" if the original had more CRLF than LF, else "\n".</summary>
        public string NewLine { get; }

        /// <summary>As returned by <see cref="TextFileReader"/>; its preamble decides the BOM.</summary>
        public Encoding Encoding { get; }

        public static DocumentText Parse(string text, Encoding encoding)
        {
            string newLine = DetectNewLine(text);
            (string frontMatter, string content) = Editing.FrontMatter.Split(ToLf(text));
            string trimmed = TrimBlankLines(content, out string leading);
            bool isRtl = RtlWrapper.TryUnwrap(trimmed, out string inner);
            return new DocumentText(frontMatter, leading, isRtl ? inner : trimmed, isRtl, newLine, encoding);
        }

        public static DocumentText CreateEmpty() =>
            new(string.Empty, string.Empty, string.Empty, false, "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        public DocumentText With(string body, bool isRtl) =>
            new(FrontMatter, _leadingBlankLines, TrimBlankLines(ToLf(body), out _), isRtl, NewLine, Encoding);

        /// <summary>The same document, to be stored as UTF-8 without a BOM.</summary>
        public DocumentText AsUtf8() =>
            new(FrontMatter, _leadingBlankLines, Body, IsRtl, NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        /// <summary>
        /// False if storing the text in <see cref="Encoding"/> would change it: a legacy code page
        /// turns what it lacks into "?" or a look-alike, and a lone surrogate fits no encoding.
        /// </summary>
        public bool SurvivesEncoding()
        {
            string text = Compose();
            try
            {
                return string.Equals(Encoding.GetString(Encoding.GetBytes(text)), text, StringComparison.Ordinal);
            }
            catch (ArgumentException)
            {
                // EncoderFallbackException and DecoderFallbackException, from a strict encoding.
                return false;
            }
        }

        /// <summary>The full file text: front matter, then the body (wrapped if RTL), <see cref="NewLine"/> applied, one trailing newline.</summary>
        public string Compose()
        {
            string content = IsRtl ? RtlWrapper.Wrap(Body) : Body;
            string text = FrontMatter + (content.Length == 0 ? string.Empty : _leadingBlankLines + content + "\n");
            if (text.Length == 0)
            {
                text = "\n";
            }

            return NewLine == "\n" ? text : text.Replace("\n", NewLine);
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
