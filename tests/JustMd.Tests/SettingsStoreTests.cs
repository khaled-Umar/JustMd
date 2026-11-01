using System.IO;
using JustMd.Core.Settings;

namespace JustMd.Tests
{
    public class SettingsStoreTests
    {
        [Fact]
        public void Round_trips_all_values()
        {
            using var temp = new TempDirectory();
            var store = new SettingsStore(temp.Combine("nested", "settings.json"));
            var settings = new AppSettings
            {
                Theme = ThemeMode.Dark,
                Zoom = 1.25,
                OutlineVisible = false,
                Window = new WindowPlacement { X = -1200, Y = 40, Width = 1000, Height = 700, Maximized = true },
            };

            Assert.True(store.Save(settings));
            AppSettings loaded = store.Load();

            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(1.25, loaded.Zoom);
            Assert.False(loaded.OutlineVisible);
            Assert.NotNull(loaded.Window);
            Assert.Equal(-1200, loaded.Window!.X);
            Assert.Equal(40, loaded.Window.Y);
            Assert.Equal(1000, loaded.Window.Width);
            Assert.Equal(700, loaded.Window.Height);
            Assert.True(loaded.Window.Maximized);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }

        [Fact]
        public void Saved_json_is_readable()
        {
            string json = SettingsStore.Serialize(new AppSettings { Theme = ThemeMode.Light });

            Assert.Contains("\"theme\": \"light\"", json);
            Assert.Contains("\"zoom\": 1", json);
            Assert.DoesNotContain("\"window\"", json);
        }

        [Fact]
        public void Overwrites_existing_file()
        {
            using var temp = new TempDirectory();
            var store = new SettingsStore(temp.Combine("settings.json"));

            store.Save(new AppSettings { Theme = ThemeMode.Dark });
            store.Save(new AppSettings { Theme = ThemeMode.Light });

            Assert.Equal(ThemeMode.Light, store.Load().Theme);
        }

        [Fact]
        public void Missing_file_gives_defaults()
        {
            using var temp = new TempDirectory();
            AppSettings loaded = new SettingsStore(temp.Combine("nope.json")).Load();

            AssertDefaults(loaded);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{\"theme\": ")]
        [InlineData("[1,2,3]")]
        [InlineData("null")]
        [InlineData("{\"zoom\": \"abc\"}")]
        [InlineData("{\"window\": 5}")]
        public void Corrupt_file_gives_defaults(string content)
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json", content);

            AssertDefaults(new SettingsStore(path).Load());
        }

        [Fact]
        public void Binary_garbage_gives_defaults()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("settings.json");
            File.WriteAllBytes(path, new byte[] { 0xFF, 0x00, 0x13, 0x37, 0xC3, 0x28 });

            AssertDefaults(new SettingsStore(path).Load());
        }

        [Fact]
        public void Partial_file_keeps_defaults_for_missing_members()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json", "{ \"theme\": \"DARK\" }");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(AppSettings.DefaultZoom, loaded.Zoom);
            Assert.True(loaded.OutlineVisible);
            Assert.Null(loaded.Window);
        }

        [Fact]
        public void Out_of_range_values_are_repaired()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json",
                "{ \"theme\": \"purple\", \"zoom\": 99, \"window\": { \"x\": 0, \"y\": 0, \"width\": 5, \"height\": 5 }, \"extra\": true }");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Equal(ThemeMode.System, loaded.Theme);
            Assert.Equal(AppSettings.MaxZoom, loaded.Zoom);
            Assert.Null(loaded.Window);
        }

        [Fact]
        public void Numeric_theme_is_not_accepted()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json", "{ \"theme\": 2, \"zoom\": 0.5 }");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Equal(ThemeMode.System, loaded.Theme);
            Assert.Equal(0.5, loaded.Zoom);
        }

        [Theory]
        [InlineData(0, AppSettings.DefaultZoom)]
        [InlineData(-3, AppSettings.DefaultZoom)]
        [InlineData(double.NaN, AppSettings.DefaultZoom)]
        [InlineData(0.1, AppSettings.MinZoom)]
        [InlineData(1.5, 1.5)]
        public void ClampZoom_keeps_zoom_sane(double input, double expected)
        {
            Assert.Equal(expected, AppSettings.ClampZoom(input));
        }

        [Fact]
        public void Save_to_unwritable_location_returns_false()
        {
            using var temp = new TempDirectory();
            string blocker = temp.WriteFile("file", "x");

            // A file where a directory is expected cannot be created.
            Assert.False(new SettingsStore(Path.Combine(blocker, "settings.json")).Save(new AppSettings()));
        }

        [Fact]
        public void Two_windows_merge_their_own_changes()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("settings.json");
            new SettingsStore(path).Save(new AppSettings { Theme = ThemeMode.Light, Zoom = 1.0, OutlineVisible = true });

            // Two app processes start and load the same file.
            var storeA = new SettingsStore(path);
            var storeB = new SettingsStore(path);
            AppSettings a = storeA.Load();
            AppSettings baseA = a.Clone();
            AppSettings b = storeB.Load();
            AppSettings baseB = b.Clone();

            a.Theme = ThemeMode.Dark;
            Assert.True(storeA.SaveChanges(a, baseA));

            b.Zoom = 1.5;
            b.OutlineWidth = 320;
            Assert.True(storeB.SaveChanges(b, baseB));

            AppSettings result = new SettingsStore(path).Load();
            Assert.Equal(ThemeMode.Dark, result.Theme);   // A's change survived B's save
            Assert.Equal(1.5, result.Zoom);
            Assert.Equal(320, result.OutlineWidth);
            Assert.True(result.OutlineVisible);
        }

        [Fact]
        public void SaveChanges_keeps_other_window_value_until_this_window_changes_it_again()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("settings.json");
            var store = new SettingsStore(path);
            store.Save(new AppSettings());

            AppSettings mine = store.Load();
            AppSettings baseline = mine.Clone();

            // Another window switches to dark.
            new SettingsStore(path).Save(new AppSettings { Theme = ThemeMode.Dark });

            mine.OutlineVisible = false;
            store.SaveChanges(mine, baseline);
            Assert.Equal(ThemeMode.Dark, store.Load().Theme);

            baseline = mine.Clone();
            mine.Theme = ThemeMode.Light;
            store.SaveChanges(mine, baseline);
            Assert.Equal(ThemeMode.Light, store.Load().Theme);
            Assert.False(store.Load().OutlineVisible);
        }

        [Fact]
        public void SaveChanges_with_missing_file_starts_from_defaults()
        {
            using var temp = new TempDirectory();
            var store = new SettingsStore(temp.Combine("settings.json"));
            var baseline = new AppSettings();
            var current = new AppSettings { Window = new WindowPlacement { X = 1, Y = 2, Width = 800, Height = 600 } };

            Assert.True(store.SaveChanges(current, baseline));

            AppSettings loaded = store.Load();
            Assert.Equal(800, loaded.Window!.Width);
            Assert.Equal(ThemeMode.System, loaded.Theme);
        }

        [Fact]
        public void SaveChanges_over_corrupt_file_writes_own_values()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json", "{ broken");
            var store = new SettingsStore(path);
            var current = new AppSettings { Theme = ThemeMode.Dark, Zoom = 2 };

            Assert.True(store.SaveChanges(current, current.Clone()));

            AppSettings loaded = store.Load();
            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(2, loaded.Zoom);
        }

        [Fact]
        public void Concurrent_saves_leave_no_temp_files_and_a_valid_file()
        {
            using var temp = new TempDirectory();
            string path = temp.Combine("settings.json");

            System.Threading.Tasks.Parallel.For(0, 24, i =>
            {
                var store = new SettingsStore(path);
                var baseline = new AppSettings();
                var current = new AppSettings { Zoom = 1 + (i % 4) * 0.25 };
                store.SaveChanges(current, baseline);
            });

            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            Assert.InRange(new SettingsStore(path).Load().Zoom, 1.0, 1.75);
        }

        [Theory]
        [InlineData(0, AppSettings.DefaultOutlineWidth)]
        [InlineData(10, AppSettings.MinOutlineWidth)]
        [InlineData(5000, AppSettings.MaxOutlineWidth)]
        [InlineData(300, 300)]
        public void Outline_width_is_clamped(int input, int expected)
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json", "{ \"outlineWidth\": " + input + " }");

            Assert.Equal(expected, new SettingsStore(path).Load().OutlineWidth);
        }

        private static void AssertDefaults(AppSettings settings)
        {
            Assert.Equal(ThemeMode.System, settings.Theme);
            Assert.Equal(AppSettings.DefaultZoom, settings.Zoom);
            Assert.True(settings.OutlineVisible);
            Assert.Null(settings.Window);
        }
    }
}
