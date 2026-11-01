using System;
using System.IO;

namespace JustMd.Core
{
    /// <summary>Per-user locations used by the app.</summary>
    public static class AppPaths
    {
        private const string SettingsFileName = "settings.json";

        /// <summary>
        /// Empty file in the new data folder: settings.json has been taken over from the 1.0.0 folder, so that
        /// folder may be deleted and the new settings win from now on.
        /// </summary>
        internal const string MigrationMarkerFileName = "migrated-from-JustMdViewer";

        /// <summary><c>%LOCALAPPDATA%\JustMd</c>.</summary>
        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustMd");

        /// <summary><c>%LOCALAPPDATA%\JustMd\WebView2</c> (WebView2 user data folder).</summary>
        public static string WebViewDataDirectory => Path.Combine(DataDirectory, "WebView2");

        /// <summary><c>%LOCALAPPDATA%\JustMdViewer</c>. Version 1.0.0 shipped under this name.</summary>
        public static string LegacyDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustMdViewer");

        /// <summary>
        /// Moves the 1.0.0 data folder to <see cref="DataDirectory"/> or, when it cannot be moved, copies its
        /// settings and removes the rest once nothing holds it. Whatever fails is retried on the next start;
        /// this never waits and never throws.
        /// </summary>
        public static void MigrateLegacyDataDirectory()
        {
            if (string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            {
                return;
            }

            MigrateLegacyDataDirectory(LegacyDataDirectory, DataDirectory);
        }

        internal static void MigrateLegacyDataDirectory(string legacyDirectory, string dataDirectory)
        {
            if (!Path.IsPathFullyQualified(legacyDirectory) || !Path.IsPathFullyQualified(dataDirectory)
                || !Directory.Exists(legacyDirectory))
            {
                return;
            }

            string marker = Path.Combine(dataDirectory, MigrationMarkerFileName);
            try
            {
                if (!File.Exists(marker))
                {
                    if (!TryMoveDirectory(legacyDirectory, dataDirectory))
                    {
                        CopyLegacySettings(legacyDirectory, dataDirectory);
                    }

                    File.WriteAllBytes(marker, Array.Empty<byte>());
                }

                // After a move the old folder is already gone.
                if (Directory.Exists(legacyDirectory))
                {
                    Directory.Delete(legacyDirectory, recursive: true);
                }

                File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Nothing is lost: without the marker the next start copies settings.json again, with it
                // the next start only finishes the cleanup.
            }
        }

        private static bool TryMoveDirectory(string legacyDirectory, string dataDirectory)
        {
            if (Directory.Exists(dataDirectory))
            {
                return false;
            }

            try
            {
                Directory.Move(legacyDirectory, dataDirectory);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Something holds the old folder open.
                return false;
            }
        }

        private static void CopyLegacySettings(string legacyDirectory, string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);

            // settings.json also holds the session (AppSettings.Session), so it is the only file needed.
            // It overwrites what the new folder has: until the marker exists those are only defaults.
            string legacySettings = Path.Combine(legacyDirectory, SettingsFileName);
            if (File.Exists(legacySettings))
            {
                File.Copy(legacySettings, Path.Combine(dataDirectory, SettingsFileName), overwrite: true);
            }
        }
    }
}
