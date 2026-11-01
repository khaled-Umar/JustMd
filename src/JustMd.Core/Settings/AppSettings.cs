using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustMd.Core.Settings
{
    public enum ThemeMode
    {
        System,
        Light,
        Dark,
    }

    /// <summary>Saved window bounds (in screen pixels) and state.</summary>
    public sealed class WindowPlacement
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("maximized")]
        public bool Maximized { get; set; }

        [JsonIgnore]
        public bool IsUsable => Width >= AppSettings.MinWindowWidth && Height >= AppSettings.MinWindowHeight
                                && Width <= 100_000 && Height <= 100_000;

        public WindowPlacement Clone() => (WindowPlacement)MemberwiseClone();

        public static bool AreEqual(WindowPlacement? a, WindowPlacement? b) =>
            ReferenceEquals(a, b)
            || (a != null && b != null && a.X == b.X && a.Y == b.Y && a.Width == b.Width
                && a.Height == b.Height && a.Maximized == b.Maximized);
    }

    /// <summary>User preferences persisted to <c>settings.json</c>.</summary>
    public sealed class AppSettings
    {
        public const double MinZoom = 0.25;
        public const double MaxZoom = 5.0;
        public const double DefaultZoom = 1.0;
        public const int MinWindowWidth = 320;
        public const int MinWindowHeight = 240;
        public const int DefaultOutlineWidth = 264;
        public const int MinOutlineWidth = 160;
        public const int MaxOutlineWidth = 640;

        [JsonPropertyName("theme")]
        [JsonConverter(typeof(LenientThemeConverter))]
        public ThemeMode Theme { get; set; } = ThemeMode.System;

        [JsonPropertyName("zoom")]
        public double Zoom { get; set; } = DefaultZoom;

        [JsonPropertyName("outlineVisible")]
        public bool OutlineVisible { get; set; } = true;

        /// <summary>Outline panel width in CSS pixels (before zoom).</summary>
        [JsonPropertyName("outlineWidth")]
        public int OutlineWidth { get; set; } = DefaultOutlineWidth;

        [JsonPropertyName("window")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WindowPlacement? Window { get; set; }

        /// <summary>Open tabs, saved whenever the tab set changes.</summary>
        [JsonPropertyName("session")]
        public SessionState Session { get; set; } = new();

        public static double ClampZoom(double zoom) =>
            double.IsNaN(zoom) || double.IsInfinity(zoom) || zoom <= 0
                ? DefaultZoom
                : Math.Clamp(zoom, MinZoom, MaxZoom);

        public static int ClampOutlineWidth(int width) =>
            width <= 0 ? DefaultOutlineWidth : Math.Clamp(width, MinOutlineWidth, MaxOutlineWidth);

        /// <summary>Repairs out-of-range values (hand-edited or corrupt files).</summary>
        public void Normalize()
        {
            if (!Enum.IsDefined(Theme))
            {
                Theme = ThemeMode.System;
            }

            Zoom = ClampZoom(Zoom);
            OutlineWidth = ClampOutlineWidth(OutlineWidth);
            Session ??= new SessionState();
            Session.Tabs ??= new List<SessionTab>();
            Session.Tabs.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Path));
            if (Session.Tabs.Count > SessionState.MaxTabs)
            {
                Session.Tabs.RemoveRange(SessionState.MaxTabs, Session.Tabs.Count - SessionState.MaxTabs);
            }

            Session.ActiveIndex = Session.Tabs.Count == 0 ? -1 : Math.Clamp(Session.ActiveIndex, 0, Session.Tabs.Count - 1);
            if (Window != null && !Window.IsUsable)
            {
                Window = null;
            }
        }

        public AppSettings Clone() => new()
        {
            Theme = Theme,
            Zoom = Zoom,
            OutlineVisible = OutlineVisible,
            OutlineWidth = OutlineWidth,
            Window = Window?.Clone(),
            Session = Session?.Clone() ?? new SessionState(),
        };
    }

    /// <summary>Reads "system"/"light"/"dark" in any case; anything unrecognised becomes System.</summary>
    internal sealed class LenientThemeConverter : JsonConverter<ThemeMode>
    {
        public override ThemeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse(reader.GetString(), ignoreCase: true, out ThemeMode mode)
                && Enum.IsDefined(mode)
                && !int.TryParse(reader.GetString(), out _))
            {
                return mode;
            }

            reader.Skip();
            return ThemeMode.System;
        }

        public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
