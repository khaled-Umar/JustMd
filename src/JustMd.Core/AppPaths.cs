using System;
using System.IO;
using System.Threading;

namespace JustMd.Core
{
    /// <summary>Per-user locations used by the app.</summary>
    public static class AppPaths
    {
        private const string SettingsFileName = "settings.json";
        private const int LegacyMoveAttempts = 6;
        private const int LegacyMoveRetryDelayMilliseconds = 500;

        /// <summary><c>%LOCALAPPDATA%\JustMd</c>.</summary>
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustMd");

        /// <summary><c>%LOCALAPPDATA%\JustMd\WebView2</c> (WebView2 user data folder).</summary>
        public static string WebViewDataDirectory => Path.Combine(DataDirectory, "WebView2");

        /// <summary><c>%LOCALAPPDATA%\JustMdViewer</c>. Version 1.0.0 shipped under this name.</summary>
        public static string LegacyDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustMdViewer");

        /// <summary>
        /// Moves the 1.0.0 data folder to <see cref="DataDirectory"/>, or removes what a locked move left behind
        /// on an earlier start. Waits about 2.5 s at most, and only while the old folder is locked. Never throws.
        /// </summary>
        public static void MigrateLegacyDataDirectory() =>
            MigrateLegacyDataDirectory(LegacyDataDirectory, DataDirectory, LegacyMoveAttempts, () => Thread.Sleep(LegacyMoveRetryDelayMilliseconds));

        internal static void MigrateLegacyDataDirectory(string legacyDirectory, string dataDirectory, int moveAttempts, Action waitBetweenAttempts)
        {
            if (!Directory.Exists(legacyDirectory))
            {
                return;
            }

            if (Directory.Exists(dataDirectory))
            {
                DeleteLeftoverLegacyDirectory(legacyDirectory, dataDirectory);
                return;
            }

            for (int attempt = 1; attempt <= moveAttempts; attempt++)
            {
                try
                {
                    Directory.Move(legacyDirectory, dataDirectory);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Something holds the old folder open, often only for a moment after 1.0.0 closes.
                }

                if (attempt < moveAttempts)
                {
                    waitBetweenAttempts();
                }
            }

            try
            {
                // Still held: fall back to copying what matters. A later start removes the leftover.
                Directory.CreateDirectory(dataDirectory);

                // settings.json also holds the session (AppSettings.Session), so it is the only file needed.
                File.Copy(Path.Combine(legacyDirectory, SettingsFileName), Path.Combine(dataDirectory, SettingsFileName), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Best effort: the app starts with defaults.
            }
        }

        private static void DeleteLeftoverLegacyDirectory(string legacyDirectory, string dataDirectory)
        {
            // Without settings.json in the new folder, the old one may still hold the only copy.
            if (!File.Exists(Path.Combine(dataDirectory, SettingsFileName)))
            {
                return;
            }

            try
            {
                Directory.Delete(legacyDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Best effort: the next start tries again.
            }
        }
    }
}
