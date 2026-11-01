using System.IO;
using JustMd.Core;

namespace JustMd.Tests
{
    public class AppPathsTests
    {
        // For every path that must not wait: any wait fails the test instead of sleeping.
        private static void MigrateWithoutWaiting(string legacy, string data) =>
            AppPaths.MigrateLegacyDataDirectory(legacy, data, moveAttempts: 6, () => Assert.Fail("Unexpected wait."));

        [Fact]
        public void Migration_moves_the_folder_with_settings_and_webview_data()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");
            temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");

            MigrateWithoutWaiting(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.Equal("prefs", File.ReadAllText(Path.Combine(data, "WebView2", "Default", "Preferences")));
        }

        [Fact]
        public void Migration_deletes_the_leftover_legacy_folder_when_the_new_one_has_settings()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "old");
            temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");
            string dataSettings = temp.WriteFile(@"JustMd\settings.json", "new");

            MigrateWithoutWaiting(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.Equal("new", File.ReadAllText(dataSettings));
        }

        [Fact]
        public void Migration_leaves_the_legacy_folder_alone_when_the_new_one_has_no_settings()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            string legacySettings = temp.WriteFile(@"JustMdViewer\settings.json", "old");
            string legacyPreferences = temp.WriteFile(@"JustMdViewer\WebView2\Default\Preferences", "prefs");
            temp.WriteFile(@"JustMd\WebView2\Default\Preferences", "new prefs");

            MigrateWithoutWaiting(legacy, data);

            Assert.Equal("old", File.ReadAllText(legacySettings));
            Assert.Equal("prefs", File.ReadAllText(legacyPreferences));
            Assert.False(File.Exists(Path.Combine(data, "settings.json")));
        }

        [Fact]
        public void Migration_cleanup_does_not_throw_on_a_locked_file_and_finishes_on_the_next_start()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            string locked = temp.WriteFile(@"JustMdViewer\WebView2\lockfile", "lock");
            temp.WriteFile(@"JustMdViewer\settings.json", "old");
            string dataSettings = temp.WriteFile(@"JustMd\settings.json", "new");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                MigrateWithoutWaiting(legacy, data);

                Assert.True(File.Exists(locked));
            }

            Assert.Equal("new", File.ReadAllText(dataSettings));

            MigrateWithoutWaiting(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.Equal("new", File.ReadAllText(dataSettings));
        }

        [Fact]
        public void Migration_does_nothing_when_the_legacy_directory_is_missing()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");

            MigrateWithoutWaiting(legacy, data);

            Assert.False(Directory.Exists(legacy));
            Assert.False(Directory.Exists(data));
        }

        [Fact]
        public void Migration_retries_and_moves_the_whole_folder_once_the_lock_is_released()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");
            string locked = temp.WriteFile(@"JustMdViewer\WebView2\lockfile", "lock");
            int waits = 0;

            using (var lockStream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AppPaths.MigrateLegacyDataDirectory(legacy, data, moveAttempts: 6, () =>
                {
                    waits++;
                    lockStream.Dispose();
                });
            }

            Assert.Equal(1, waits);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.Equal("lock", File.ReadAllText(Path.Combine(data, "WebView2", "lockfile")));
        }

        [Fact]
        public void Migration_copies_settings_when_every_move_attempt_fails()
        {
            using var temp = new TempDirectory();
            string legacy = temp.Combine("JustMdViewer");
            string data = temp.Combine("JustMd");
            string legacySettings = temp.WriteFile(@"JustMdViewer\settings.json", "{\"theme\":1}");
            string locked = temp.WriteFile(@"JustMdViewer\WebView2\lockfile", "lock");
            int waits = 0;

            // An open handle inside the folder makes the rename fail on Windows.
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AppPaths.MigrateLegacyDataDirectory(legacy, data, moveAttempts: 4, () => waits++);
            }

            // One wait between each pair of attempts, none after the last.
            Assert.Equal(3, waits);
            Assert.Equal("{\"theme\":1}", File.ReadAllText(legacySettings));
            Assert.Equal("{\"theme\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(data, "WebView2")));
        }
    }
}
