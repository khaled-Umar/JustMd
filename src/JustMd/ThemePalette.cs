using System.Drawing;
using JustMd.Core.Settings;
using Microsoft.Win32;

namespace JustMd
{
    /// <summary>Native colours that must match viewer.css, plus Windows theme detection.</summary>
    internal sealed class ThemePalette
    {
        public static readonly ThemePalette Light = new(
            dark: false,
            background: Color.FromArgb(0xFF, 0xFF, 0xFF),
            surface: Color.FromArgb(0xF6, 0xF8, 0xFA),
            text: Color.FromArgb(0x1F, 0x23, 0x28),
            muted: Color.FromArgb(0x59, 0x63, 0x6E),
            border: Color.FromArgb(0xD1, 0xD9, 0xE0),
            accent: Color.FromArgb(0x25, 0x63, 0xEB),
            accentText: Color.White,
            warning: Color.FromArgb(0x9A, 0x67, 0x00));

        public static readonly ThemePalette Dark = new(
            dark: true,
            background: Color.FromArgb(0x0D, 0x11, 0x17),
            surface: Color.FromArgb(0x15, 0x1B, 0x23),
            text: Color.FromArgb(0xE6, 0xED, 0xF3),
            muted: Color.FromArgb(0x91, 0x98, 0xA1),
            border: Color.FromArgb(0x30, 0x36, 0x3D),
            accent: Color.FromArgb(0x60, 0xA5, 0xFA),
            accentText: Color.FromArgb(0x0B, 0x12, 0x20),
            warning: Color.FromArgb(0xD2, 0x99, 0x22));

        private ThemePalette(bool dark, Color background, Color surface, Color text, Color muted,
                             Color border, Color accent, Color accentText, Color warning)
        {
            IsDark = dark;
            Background = background;
            Surface = surface;
            Text = text;
            Muted = muted;
            Border = border;
            Accent = accent;
            AccentText = accentText;
            Warning = warning;
        }

        public bool IsDark { get; }

        public Color Background { get; }

        public Color Surface { get; }

        public Color Text { get; }

        public Color Muted { get; }

        public Color Border { get; }

        public Color Accent { get; }

        public Color AccentText { get; }

        public Color Warning { get; }

        public string CssName => IsDark ? "dark" : "light";

        public static ThemePalette For(ThemeMode mode) => mode switch
        {
            ThemeMode.Dark => Dark,
            ThemeMode.Light => Light,
            _ => IsWindowsAppThemeDark() ? Dark : Light,
        };

        /// <summary>Reads the Windows "app mode" (Settings > Personalization > Colors).</summary>
        public static bool IsWindowsAppThemeDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}
