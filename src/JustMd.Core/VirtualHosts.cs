namespace JustMd.Core
{
    /// <summary>
    /// Host names the viewer uses inside WebView2. They use the reserved <c>.example</c> TLD:
    /// WebView2 recommends against <c>.local</c>, which can add a multi-second DNS delay.
    /// </summary>
    public static class VirtualHosts
    {
        /// <summary>Host mapped to the bundled <c>web</c> folder (viewer page, scripts, styles).</summary>
        public const string App = "app.justmd.example";

        /// <summary>Host that serves local images referenced by the open document.</summary>
        public const string Images = "img.justmd.example";

        public const string AppOrigin = "https://" + App;

        public const string ViewerUrl = AppOrigin + "/viewer.html";
    }
}
