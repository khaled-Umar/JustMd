using System;
using System.IO;
using JustMd.Core;

namespace JustMd.Tests
{
    public class AppPathsTests
    {
        private static string MarkerIn(string data) => Path.Combine(data, AppPaths.MigrationMarkerFileName);

        // An open handle inside the folder makes the rename fail on Windows.
        private static FileStream HoldOpen(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        [Fact]
        public void Migration_moves_the_folder_with_settings_and_webview_data()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");
            temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.Equal("prefs", File.ReadAllText(Path.Combine(data, "WebView2", "Default", "Preferences")));
            Assert.False(File.Exists(MarkerIn(data)));
        }

        [Fact]
        public void Migration_copies_settings_when_the_move_fails_and_finishes_on_the_next_start()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");
            string locked = temp.WriteFile(@"JustMdViewer\WebView2\lockfile", "lock");

            using (HoldOpen(locked))
            {
                AppPaths.MigrateLegacyDataDirectory(legacy, data);

                Assert.True(File.Exists(locked));
                Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
                Assert.False(Directory.Exists(Path.Combine(data, "WebView2")));
                Assert.True(File.Exists(MarkerIn(data)));
            }

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.False(File.Exists(MarkerIn(data)));
            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
        }

        [Fact]
        public void Migration_keeps_the_legacy_settings_authoritative_until_a_copy_has_succeeded()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            string legacySettings = temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");

            using (new FileStream(legacySettings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AppPaths.MigrateLegacyDataDirectory(legacy, data);

                Assert.True(Directory.Exists(data));
                Assert.False(File.Exists(Path.Combine(data, "settings.json")));
                Assert.False(File.Exists(MarkerIn(data)));
            }

            // The app ran on defaults in between and saved them.
            File.WriteAllText(Path.Combine(data, "settings.json"), "defaults");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.False(Directory.Exists(legacy));
            Assert.False(File.Exists(MarkerIn(data)));
        }

        [Fact]
        public void Migration_overwrites_new_settings_while_there_is_no_marker()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "old");
            string dataSettings = temp.WriteFile(@"JustMd\settings.json", "new");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.Equal("old", File.ReadAllText(dataSettings));
            Assert.False(Directory.Exists(legacy));
            Assert.False(File.Exists(MarkerIn(data)));
        }

        [Fact]
        public void Migration_keeps_new_settings_and_deletes_the_legacy_folder_when_the_marker_exists()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "old");
            temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");
            string dataSettings = temp.WriteFile(@"JustMd\settings.json", "new");
            File.WriteAllBytes(MarkerIn(data), Array.Empty<byte>());

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.Equal("new", File.ReadAllText(dataSettings));
            Assert.False(Directory.Exists(legacy));
            Assert.False(File.Exists(MarkerIn(data)));
        }

        [Fact]
        public void Migration_deletes_a_legacy_folder_without_settings()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");
            temp.WriteFile(@"JustMd\WebView2\Default\Preferences", "new prefs");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.False(File.Exists(Path.Combine(data, "settings.json")));
            Assert.False(File.Exists(MarkerIn(data)));
        }

        [Fact]
        public void Migration_does_nothing_when_the_legacy_directory_is_missing()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.False(Directory.Exists(data));
        }

        [Fact]
        public void Migration_does_nothing_when_the_legacy_path_is_relative()
        {
            using var temp = new TempDirectory();
            string data = temp.Combine("JustMd");

            // A real folder under the working directory, so only the path check can stop the migration.
            string legacy = "JustMdViewer-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(legacy);
            try
            {
                File.WriteAllText(Path.Combine(legacy, "settings.json"), "old");

                AppPaths.MigrateLegacyDataDirectory(legacy, data);

                Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "settings.json")));
                Assert.False(Directory.Exists(data));
            }
            finally
            {
                Directory.Delete(legacy, recursive: true);
            }
        }

        [Fact]
        public void Migration_does_nothing_when_the_data_path_is_relative()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string legacySettings = temp.WriteFile(@"JustMdViewer\settings.json", "old");
            string data = "JustMd-" + Guid.NewGuid().ToString("N");

            AppPaths.MigrateLegacyDataDirectory(legacy, data);

            Assert.Equal("old", File.ReadAllText(legacySettings));
            Assert.False(Directory.Exists(data));
        }
    }
}
