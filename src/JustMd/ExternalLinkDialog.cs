using System;
using System.Drawing;
using System.Windows.Forms;
using JustMd.Core;

namespace JustMd
{
    internal enum ExternalLinkChoice
    {
        Cancel,
        Open,
        Copy,
    }

    /// <summary>
    /// Confirmation shown before an external link leaves the app. The real host name is shown
    /// large and highlighted (user-info tricks like <c>https://bank.com@evil.example</c> and
    /// look-alike Unicode names are made visible). Cancel is the default button; Esc cancels.
    /// </summary>
    internal sealed class ExternalLinkDialog : Form
    {
        private readonly float _scale;
        private readonly ThemePalette _palette;
        private readonly Font _baseFont;
        private readonly Font _headingFont;
        private readonly Font _captionFont;
        private readonly Font _hostFont;
        private readonly Font _urlFont;
        private readonly Font _urlBoldFont;
        private readonly Button _cancelButton;

        public ExternalLinkDialog(Uri uri, ThemePalette palette, int dpi)
        {
            _scale = Math.Max(1f, dpi / 96f);
            _palette = palette;

            _baseFont = new Font("Segoe UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _headingFont = new Font("Segoe UI Semibold", 16f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _captionFont = new Font("Segoe UI Semibold", 10.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _hostFont = new Font("Segoe UI", 20f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _urlFont = new Font("Cascadia Mono", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _urlBoldFont = new Font(_urlFont, FontStyle.Bold);

            bool isMail = uri.Scheme == Uri.UriSchemeMailto;
            ExternalLinkParts parts = LinkClassifier.GetDisplayParts(uri);
            Url = uri;

            Text = isMail ? "Send an email" : "Open external link";
            AutoScaleMode = AutoScaleMode.None;
            Font = _baseFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(S(24), S(20), S(24), S(18));
            BackColor = palette.Background;
            ForeColor = palette.Text;

            int width = S(520);
            var layout = new TableLayoutPanel
            {
                // Form.Padding only affects docked children; place the panel inside it explicitly.
                Location = new Point(Padding.Left, Padding.Top),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = palette.Background,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));

            layout.Controls.Add(MakeLabel(isMail ? "Send an email to this address?" : "Open this link in your browser?",
                _headingFont, palette.Text, width, S(0)));
            layout.Controls.Add(MakeLabel(
                isMail
                    ? "This link opens your email app. Check the address before you continue."
                    : "This link leads outside JustMd. Check where it goes before you continue.",
                _baseFont, palette.Muted, width, S(6)));

            layout.Controls.Add(MakeLabel(isMail ? "EMAIL DOMAIN" : "WEBSITE", _captionFont, palette.Muted, width, S(18)));
            layout.Controls.Add(MakeLabel(parts.Host, _hostFont, palette.Accent, width, S(2)));

            if (!string.Equals(uri.IdnHost, uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                layout.Controls.Add(MakeLabel(
                    "This name contains international characters. Its actual address is " + uri.IdnHost + ".",
                    _baseFont, palette.Warning, width, S(4)));
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) && !isMail)
            {
                layout.Controls.Add(MakeLabel(
                    "Note: the text before \"@\" is not the website. The link goes to " + parts.Host + ".",
                    _baseFont, palette.Warning, width, S(4)));
            }

            layout.Controls.Add(MakeLabel(isMail ? "FULL LINK" : "FULL ADDRESS", _captionFont, palette.Muted, width, S(16)));
            layout.Controls.Add(MakeUrlBox(parts, width));

            _cancelButton = MakeButton("Cancel", ExternalLinkChoice.Cancel, DialogResult.Cancel);
            Button copyButton = MakeButton("&Copy link", ExternalLinkChoice.Copy, DialogResult.OK);
            Button openButton = MakeButton(isMail ? "&Open in mail app" : "&Open in browser", ExternalLinkChoice.Open, DialogResult.OK);
            _cancelButton.FlatAppearance.BorderColor = palette.Accent;

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, S(22), 0, 0),
                Padding = Padding.Empty,
                Width = width,
                Anchor = AnchorStyles.Right,
                BackColor = palette.Background,
            };
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(openButton);
            layout.Controls.Add(buttons);

            Controls.Add(layout);

            AcceptButton = _cancelButton;
            CancelButton = _cancelButton;
        }

        public Uri Url { get; }

        public ExternalLinkChoice Choice { get; private set; } = ExternalLinkChoice.Cancel;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetDarkTitleBar(Handle, _palette.IsDark, repaint: false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ActiveControl = _cancelButton;
            _cancelButton.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _baseFont.Dispose();
                _headingFont.Dispose();
                _captionFont.Dispose();
                _hostFont.Dispose();
                _urlBoldFont.Dispose();
                _urlFont.Dispose();
            }
        }

        private int S(int value) => (int)Math.Round(value * _scale);

        private static Label MakeLabel(string text, Font font, Color color, int width, int topMargin) => new()
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, topMargin, 0, 0),
            UseMnemonic = false,
            AutoEllipsis = false,
        };

        private Control MakeUrlBox(ExternalLinkParts parts, int width)
        {
            var frame = new Panel
            {
                BackColor = _palette.Border,
                Padding = new Padding(1),
                Margin = new Padding(0, S(6), 0, 0),
                Width = width,
            };

            var inner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _palette.Surface,
                Padding = new Padding(S(10), S(8), S(6), S(8)),
            };

            var box = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                Font = _urlFont,
                DetectUrls = false,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = false,
                ShortcutsEnabled = true,
            };

            AppendRun(box, parts.Prefix, _urlFont, _palette.Muted);
            AppendRun(box, parts.Host, _urlBoldFont, _palette.Accent);
            AppendRun(box, parts.Suffix, _urlFont, _palette.Muted);
            box.Select(0, 0);

            // Grow to fit up to five lines, then scroll.
            int lineHeight = _urlFont.Height;
            int chrome = inner.Padding.Vertical + frame.Padding.Vertical;
            frame.Height = lineHeight + chrome + S(2);
            box.ContentsResized += (_, e) =>
                frame.Height = Math.Min(e.NewRectangle.Height, lineHeight * 5) + chrome + S(2);

            inner.Controls.Add(box);
            frame.Controls.Add(inner);
            return frame;
        }

        private static void AppendRun(RichTextBox box, string text, Font font, Color color)
        {
            if (text.Length == 0)
            {
                return;
            }

            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
        }

        private Button MakeButton(string text, ExternalLinkChoice choice, DialogResult result)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(S(100), S(32)),
                Padding = new Padding(S(10), 0, S(10), 0),
                Margin = new Padding(S(8), 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                Font = _baseFont,
                UseVisualStyleBackColor = false,
                DialogResult = result,
            };
            button.FlatAppearance.BorderColor = _palette.Border;
            button.FlatAppearance.MouseOverBackColor = _palette.IsDark ? Color.FromArgb(0x1C, 0x23, 0x2C) : Color.FromArgb(0xEA, 0xEE, 0xF2);
            button.FlatAppearance.MouseDownBackColor = _palette.IsDark ? Color.FromArgb(0x26, 0x2E, 0x38) : Color.FromArgb(0xDF, 0xE4, 0xEA);
            button.Click += (_, _) => Choice = choice;
            return button;
        }
    }
}
