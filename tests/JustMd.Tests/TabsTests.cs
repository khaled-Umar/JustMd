using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JustMd.Core.Messaging;
using JustMd.Core.Settings;
using JustMd.Core.Tabs;

namespace JustMd.Tests
{
    public class DocumentPathsTests
    {
        [Theory]
        [InlineData(@"C:\docs\a.md", @"C:\docs\a.md")]
        [InlineData(@"C:\docs\sub\..\a.md", @"C:\docs\a.md")]
        [InlineData(@"C:/docs/a.md", @"C:\docs\a.md")]
        [InlineData("\"C:\\docs\\a.md\"", @"C:\docs\a.md")]
        [InlineData(@"C:\docs\", @"C:\docs")]
        [InlineData(@"C:\", @"C:\")]
        [InlineData("file:///C:/docs/a%20b.md", @"C:\docs\a b.md")]
        public void Normalizes_paths(string input, string expected)
        {
            Assert.Equal(expected, DocumentPaths.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a\0b")]
        public void Invalid_paths_normalize_to_null(string? input)
        {
            Assert.Null(DocumentPaths.Normalize(input));
        }

        [Fact]
        public void Comparison_is_case_insensitive_and_normalised()
        {
            Assert.True(DocumentPaths.AreSame(@"C:\Docs\README.md", @"c:\docs\x\..\readme.MD"));
            Assert.False(DocumentPaths.AreSame(@"C:\docs\a.md", @"C:\docs\b.md"));
            Assert.False(DocumentPaths.AreSame(null, null));
        }
    }

    public class TabCollectionTests
    {
        [Fact]
        public void Opening_same_file_twice_returns_existing_tab()
        {
            var tabs = new TabCollection();

            DocumentTab a = tabs.Open(@"C:\docs\A.md", out bool first)!;
            DocumentTab again = tabs.Open(@"c:\DOCS\a.md", out bool second)!;

            Assert.False(first);
            Assert.True(second);
            Assert.Same(a, again);
            Assert.Equal(1, tabs.Count);
            Assert.Null(tabs.Active); // opening does not activate
        }

        [Fact]
        public void Tab_ids_are_unique_and_stable()
        {
            var tabs = new TabCollection();
            DocumentTab a = tabs.Open(@"C:\a.md", out _)!;
            DocumentTab b = tabs.Open(@"C:\b.md", out _)!;
            tabs.Close(a);
            DocumentTab c = tabs.Open(@"C:\a.md", out _)!;

            Assert.NotEqual(a.Id, b.Id);
            Assert.NotEqual(a.Id, c.Id);
            Assert.Same(b, tabs.FindById(b.Id));
            Assert.Null(tabs.FindById(a.Id));
        }

        [Fact]
        public void Closing_active_tab_activates_right_neighbour_then_left()
        {
            var tabs = Make(out DocumentTab a, out DocumentTab b, out DocumentTab c);
            tabs.Activate(b);

            Assert.Same(c, tabs.Close(b));
            Assert.Same(a, tabs.Close(c));
            Assert.Null(tabs.Close(a));
            Assert.Equal(0, tabs.Count);
            Assert.Null(tabs.Active);
        }

        [Fact]
        public void Closing_inactive_tab_keeps_active()
        {
            var tabs = Make(out DocumentTab a, out DocumentTab b, out _);
            tabs.Activate(a);

            Assert.Same(a, tabs.Close(b));
        }

        [Fact]
        public void Cycle_wraps_around()
        {
            var tabs = Make(out DocumentTab a, out _, out DocumentTab c);
            tabs.Activate(c);

            Assert.Same(a, tabs.Cycle(+1));
            tabs.Activate(a);
            Assert.Same(c, tabs.Cycle(-1));
        }

        [Fact]
        public void ByNumber_follows_browser_convention()
        {
            var tabs = Make(out DocumentTab a, out DocumentTab b, out DocumentTab c);

            Assert.Same(a, tabs.ByNumber(1));
            Assert.Same(b, tabs.ByNumber(2));
            Assert.Null(tabs.ByNumber(4));
            Assert.Same(c, tabs.ByNumber(9));
            Assert.Null(tabs.ByNumber(0));
            Assert.Null(new TabCollection().ByNumber(9));
        }

        [Fact]
        public void Move_reorders()
        {
            var tabs = Make(out DocumentTab a, out DocumentTab b, out DocumentTab c);

            tabs.Move(a, 2);

            Assert.Equal(new[] { b, c, a }, tabs.Tabs);
        }

        [Fact]
        public void Invalid_path_is_not_opened()
        {
            Assert.Null(new TabCollection().Open("", out _));
        }

        [Fact]
        public void Untitled_numbers_count_up_and_are_not_reused()
        {
            var tabs = new TabCollection();
            DocumentTab one = tabs.NewUntitled();
            DocumentTab two = tabs.NewUntitled();
            tabs.Close(two);
            DocumentTab three = tabs.NewUntitled();

            Assert.Equal(1, one.UntitledNumber);
            Assert.Equal(3, three.UntitledNumber);
            Assert.Equal("Untitled 3", three.DisplayName);
            Assert.True(three.IsUntitled);
            Assert.Null(three.Path);
            Assert.Equal(string.Empty, three.FileName);
            Assert.NotEqual(one.Id, three.Id);
            Assert.Equal(new[] { one, three }, tabs.Tabs);
            Assert.Null(tabs.Active); // like Open, does not activate
        }

        [Fact]
        public void File_tabs_show_their_file_name()
        {
            DocumentTab tab = new TabCollection().Open(@"C:\docs\a.md", out _)!;

            Assert.False(tab.IsUntitled);
            Assert.Equal("a.md", tab.DisplayName);
            Assert.Equal(0, tab.UntitledNumber);
        }

        [Fact]
        public void Find_never_returns_untitled()
        {
            var tabs = new TabCollection();
            tabs.NewUntitled();

            Assert.Null(tabs.Find(null));
            Assert.Null(tabs.Find(""));
            Assert.Null(tabs.Find("Untitled 1"));
            Assert.Null(tabs.Find(@"C:\docs\a.md"));
        }

        [Fact]
        public void SetPath_normalises_and_makes_the_tab_findable()
        {
            var tabs = new TabCollection();
            DocumentTab tab = tabs.NewUntitled();

            Assert.True(tabs.SetPath(tab, @"C:\docs\sub\..\a.md", out DocumentTab? displaced));

            Assert.Null(displaced);
            Assert.Equal(@"C:\docs\a.md", tab.Path);
            Assert.False(tab.IsUntitled);
            Assert.Equal("a.md", tab.DisplayName);
            Assert.Same(tab, tabs.Find(@"c:\DOCS\a.md"));
        }

        [Fact]
        public void SetPath_onto_another_open_file_removes_that_tab()
        {
            var tabs = new TabCollection();
            DocumentTab other = tabs.Open(@"C:\docs\a.md", out _)!;
            DocumentTab tab = tabs.NewUntitled();
            tabs.Activate(tab);

            Assert.True(tabs.SetPath(tab, @"c:\DOCS\A.md", out DocumentTab? displaced));

            Assert.Same(other, displaced);
            Assert.Equal(new[] { tab }, tabs.Tabs);
            Assert.Same(tab, tabs.Active);
            Assert.Same(tab, tabs.Find(@"C:\docs\a.md"));
        }

        [Fact]
        public void SetPath_onto_its_own_file_displaces_nothing()
        {
            var tabs = new TabCollection();
            DocumentTab tab = tabs.Open(@"C:\docs\a.md", out _)!;

            Assert.True(tabs.SetPath(tab, @"C:\DOCS\a.md", out DocumentTab? displaced));

            Assert.Null(displaced);
            Assert.Equal(1, tabs.Count);
        }

        [Fact]
        public void SetPath_with_invalid_path_changes_nothing()
        {
            var tabs = new TabCollection();
            DocumentTab tab = tabs.NewUntitled();

            Assert.False(tabs.SetPath(tab, "  ", out DocumentTab? displaced));

            Assert.Null(displaced);
            Assert.True(tab.IsUntitled);
        }

        [Fact]
        public void SetPath_rejects_a_foreign_tab()
        {
            DocumentTab foreign = new TabCollection().NewUntitled();

            Assert.Throws<ArgumentException>(() => new TabCollection().SetPath(foreign, @"C:\a.md", out _));
        }

        private static TabCollection Make(out DocumentTab a, out DocumentTab b, out DocumentTab c)
        {
            var tabs = new TabCollection();
            a = tabs.Open(@"C:\a.md", out _)!;
            b = tabs.Open(@"C:\b.md", out _)!;
            c = tabs.Open(@"C:\c.md", out _)!;
            return tabs;
        }
    }

    public class InstanceProtocolTests
    {
        [Fact]
        public void Round_trips_files_through_a_frame()
        {
            var files = new[] { @"C:\docs\a.md", @"\\server\share\b.markdown", @"D:\ünïcode\c d.md" };

            using var stream = new MemoryStream(InstanceProtocol.Encode(files));
            byte[]? body = InstanceProtocol.ReadFrame(stream);

            Assert.Equal(files, InstanceProtocol.Parse(body));
        }

        [Fact]
        public void Empty_file_list_is_valid_activation_message()
        {
            using var stream = new MemoryStream(InstanceProtocol.Encode(Array.Empty<string>()));

            Assert.Empty(InstanceProtocol.Parse(InstanceProtocol.ReadFrame(stream))!);
        }

        [Theory]
        [InlineData("{\"files\":[\"relative.md\"]}")]
        [InlineData("{\"files\":[\"C:relative.md\"]}")]
        [InlineData("{\"files\":[\"\\\\\\\\?\\\\C:\\\\a.md\"]}")]
        [InlineData("{\"files\":[\"C:\\\\a.md:stream\"]}")]
        [InlineData("{\"files\":[\"C:\\\\a\\u0007.md\"]}")]
        [InlineData("{\"files\":[null]}")]
        [InlineData("{\"files\":[\"\"]}")]
        [InlineData("{\"files\":\"C:\\\\a.md\"}")]
        [InlineData("{}")]
        [InlineData("[]")]
        [InlineData("not json")]
        public void Rejects_invalid_payloads(string json)
        {
            Assert.Null(InstanceProtocol.Parse(Encoding.UTF8.GetBytes(json)));
        }

        [Fact]
        public void Rejects_too_many_files()
        {
            var files = Enumerable.Range(0, InstanceProtocol.MaxFiles + 1).Select(i => @"C:\f" + i + ".md").ToList();
            string json = System.Text.Json.JsonSerializer.Serialize(new { files });

            Assert.Null(InstanceProtocol.Parse(Encoding.UTF8.GetBytes(json)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(InstanceProtocol.MaxPayloadBytes + 1)]
        public void Rejects_bad_frame_lengths(int length)
        {
            var bytes = new List<byte>(BitConverter.GetBytes(length));
            bytes.AddRange(new byte[16]);

            Assert.Null(InstanceProtocol.ReadFrame(new MemoryStream(bytes.ToArray())));
        }

        [Fact]
        public void Rejects_truncated_frame()
        {
            byte[] frame = InstanceProtocol.Encode(new[] { @"C:\a.md" });

            Assert.Null(InstanceProtocol.ReadFrame(new MemoryStream(frame, 0, frame.Length - 3)));
        }

        [Fact]
        public void Names_are_per_user_and_session_and_safe()
        {
            string a = InstanceProtocol.PipeName("S-1-5-21-1", 1);
            string b = InstanceProtocol.PipeName("S-1-5-21-2", 1);
            string c = InstanceProtocol.PipeName("S-1-5-21-1", 2);

            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.DoesNotContain("\\", InstanceProtocol.PipeName("dom\\user name", 1));
            Assert.StartsWith(@"Local\", InstanceProtocol.MutexName("S-1-5-21-1", 1));
        }
    }

    public class SessionCaptureTests
    {
        [Fact]
        public void Saves_file_tabs_in_order_with_rounded_scroll()
        {
            var tabs = new TabCollection();
            DocumentTab a = tabs.Open(@"C:\a.md", out _)!;
            DocumentTab b = tabs.Open(@"C:\b.md", out _)!;
            a.View = new TabViewState(10.6, 0, Array.Empty<string>());

            SessionState state = SessionState.From(tabs.Tabs, b);

            Assert.Equal(new[] { @"C:\a.md", @"C:\b.md" }, state.Tabs.Select(t => t.Path));
            Assert.Equal(11, state.Tabs[0].ScrollTop);
            Assert.Equal(0, state.Tabs[1].ScrollTop);
            Assert.Equal(1, state.ActiveIndex);
        }

        [Fact]
        public void Active_index_counts_only_saved_tabs()
        {
            var tabs = new TabCollection();
            tabs.NewUntitled();
            tabs.Open(@"C:\a.md", out _);
            tabs.NewUntitled();
            DocumentTab b = tabs.Open(@"C:\b.md", out _)!;

            SessionState state = SessionState.From(tabs.Tabs, b);

            Assert.Equal(2, state.Tabs.Count);
            Assert.Equal(1, state.ActiveIndex);
        }

        [Fact]
        public void Active_untitled_falls_back_to_nearest_preceding_saved_tab()
        {
            var tabs = new TabCollection();
            tabs.Open(@"C:\a.md", out _);
            tabs.Open(@"C:\b.md", out _);
            tabs.NewUntitled();
            DocumentTab active = tabs.NewUntitled();
            tabs.Open(@"C:\c.md", out _);

            Assert.Equal(1, SessionState.From(tabs.Tabs, active).ActiveIndex);
        }

        [Fact]
        public void Active_untitled_with_no_saved_tab_before_it_gives_minus_one()
        {
            var tabs = new TabCollection();
            DocumentTab active = tabs.NewUntitled();
            tabs.Open(@"C:\a.md", out _);

            SessionState state = SessionState.From(tabs.Tabs, active);

            Assert.Single(state.Tabs);
            Assert.Equal(-1, state.ActiveIndex);
        }

        [Fact]
        public void No_active_tab_gives_minus_one()
        {
            var tabs = new TabCollection();
            tabs.Open(@"C:\a.md", out _);

            Assert.Equal(-1, SessionState.From(tabs.Tabs, null).ActiveIndex);
            Assert.Equal(-1, SessionState.From(tabs.Tabs, new TabCollection().NewUntitled()).ActiveIndex);
        }

        [Fact]
        public void Only_untitled_tabs_give_an_empty_session()
        {
            var tabs = new TabCollection();
            DocumentTab active = tabs.NewUntitled();

            SessionState state = SessionState.From(tabs.Tabs, active);

            Assert.Empty(state.Tabs);
            Assert.Equal(-1, state.ActiveIndex);
        }
    }

    public class StartupPlanTests
    {
        private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\docs\a.md", @"C:\docs\b.md", @"C:\docs\c.md", @"C:\docs\new.md",
        };

        private static bool Exists(string path) => Existing.Contains(path);

        private static SessionState Session(int active, params string[] paths) => new()
        {
            Tabs = paths.Select((p, i) => new SessionTab { Path = p, ScrollTop = (i + 1) * 100 }).ToList(),
            ActiveIndex = active,
        };

        [Fact]
        public void Restores_previous_tabs_in_order_with_active_and_scroll()
        {
            var (tabs, active) = StartupPlan.Build(Session(1, @"C:\docs\a.md", @"C:\docs\b.md"), Array.Empty<string>(), Exists);

            Assert.Equal(new[] { @"C:\docs\a.md", @"C:\docs\b.md" }, tabs.Select(t => t.Path));
            Assert.Equal(1, active);
            Assert.Equal(100, tabs[0].ScrollTop);
            Assert.Equal(200, tabs[1].ScrollTop);
        }

        [Fact]
        public void Missing_files_are_skipped_and_active_follows_its_file()
        {
            var (tabs, active) = StartupPlan.Build(
                Session(2, @"C:\docs\gone.md", @"C:\docs\a.md", @"C:\docs\b.md"), Array.Empty<string>(), Exists);

            Assert.Equal(new[] { @"C:\docs\a.md", @"C:\docs\b.md" }, tabs.Select(t => t.Path));
            Assert.Equal(1, active); // still b.md
        }

        [Fact]
        public void Active_missing_file_falls_back_to_previous_tab()
        {
            var (tabs, active) = StartupPlan.Build(
                Session(1, @"C:\docs\a.md", @"C:\docs\gone.md", @"C:\docs\b.md"), Array.Empty<string>(), Exists);

            Assert.Equal(2, tabs.Count);
            Assert.Equal(0, active); // a.md, the nearest remaining tab before it
        }

        [Fact]
        public void Argument_files_are_added_after_restored_tabs_and_last_is_active()
        {
            var (tabs, active) = StartupPlan.Build(
                Session(0, @"C:\docs\a.md", @"C:\docs\b.md"), new[] { @"C:\docs\new.md", @"C:\docs\c.md" }, Exists);

            Assert.Equal(new[] { @"C:\docs\a.md", @"C:\docs\b.md", @"C:\docs\new.md", @"C:\docs\c.md" }, tabs.Select(t => t.Path));
            Assert.Equal(3, active);
            Assert.Null(tabs[2].ScrollTop);
        }

        [Fact]
        public void Argument_already_open_activates_existing_tab_without_duplicate()
        {
            var (tabs, active) = StartupPlan.Build(
                Session(1, @"C:\docs\a.md", @"C:\docs\b.md"), new[] { @"c:\DOCS\A.md" }, Exists);

            Assert.Equal(2, tabs.Count);
            Assert.Equal(0, active);
        }

        [Fact]
        public void Duplicate_session_entries_are_collapsed()
        {
            var (tabs, _) = StartupPlan.Build(
                Session(0, @"C:\docs\a.md", @"C:\DOCS\A.MD", @"C:\docs\b.md"), Array.Empty<string>(), Exists);

            Assert.Equal(2, tabs.Count);
        }

        [Fact]
        public void Missing_argument_file_is_still_opened_to_show_error()
        {
            var (tabs, active) = StartupPlan.Build(null, new[] { @"C:\docs\missing.md" }, Exists);

            Assert.Single(tabs);
            Assert.Equal(0, active);
        }

        [Fact]
        public void Nothing_to_open_gives_empty_plan()
        {
            var (tabs, active) = StartupPlan.Build(new SessionState(), Array.Empty<string>(), Exists);

            Assert.Empty(tabs);
            Assert.Equal(-1, active);
        }

        [Fact]
        public void Session_round_trips_through_settings()
        {
            using var temp = new TempDirectory();
            var store = new SettingsStore(temp.Combine("settings.json"));
            var settings = new AppSettings { Session = Session(1, @"C:\docs\a.md", @"C:\docs\b.md") };

            store.Save(settings);
            AppSettings loaded = store.Load();

            Assert.Equal(2, loaded.Session.Tabs.Count);
            Assert.Equal(@"C:\docs\b.md", loaded.Session.Tabs[1].Path);
            Assert.Equal(200, loaded.Session.Tabs[1].ScrollTop);
            Assert.Equal(1, loaded.Session.ActiveIndex);
        }

        [Fact]
        public void Corrupt_session_values_are_repaired()
        {
            using var temp = new TempDirectory();
            string path = temp.WriteFile("settings.json",
                "{ \"session\": { \"tabs\": [ null, { \"path\": \"\" }, { \"path\": \"C:\\\\a.md\" } ], \"activeIndex\": 7 } }");

            AppSettings loaded = new SettingsStore(path).Load();

            Assert.Single(loaded.Session.Tabs);
            Assert.Equal(0, loaded.Session.ActiveIndex);
        }
    }

    public class TabMessageTests
    {
        [Fact]
        public void Parses_tab_messages()
        {
            Assert.Equal(5, WebMessageParser.Parse("{\"type\":\"activateTab\",\"tabId\":5}")!.TabId);
            Assert.Equal(WebMessageType.CloseTab, WebMessageParser.Parse("{\"type\":\"closeTab\",\"tabId\":2}")!.Type);

            WebMessage state = WebMessageParser.Parse(
                "{\"type\":\"tabState\",\"tabId\":3,\"scrollTop\":420.5,\"outlineScrollTop\":12,\"collapsed\":[\"a\",\"b\"]}")!;
            Assert.Equal(WebMessageType.TabState, state.Type);
            Assert.Equal(3, state.TabId);
            Assert.Equal(420.5, state.View!.ScrollTop);
            Assert.Equal(12, state.View.OutlineScrollTop);
            Assert.Equal(new[] { "a", "b" }, state.View.CollapsedKeys);
        }

        [Theory]
        [InlineData("{\"type\":\"activateTab\"}")]
        [InlineData("{\"type\":\"activateTab\",\"tabId\":0}")]
        [InlineData("{\"type\":\"closeTab\",\"tabId\":-1}")]
        [InlineData("{\"type\":\"tabState\",\"scrollTop\":1}")]
        [InlineData("{\"type\":\"tabState\",\"tabId\":1,\"collapsed\":[null]}")]
        [InlineData("{\"type\":\"tabState\",\"tabId\":1,\"collapsed\":\"a\"}")]
        public void Rejects_invalid_tab_messages(string json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Fact]
        public void Negative_scroll_is_clamped()
        {
            WebMessage state = WebMessageParser.Parse("{\"type\":\"tabState\",\"tabId\":1,\"scrollTop\":-50}")!;

            Assert.Equal(0, state.View!.ScrollTop);
        }

        [Fact]
        public void Tabs_message_serialises_tab_list()
        {
            string json = new HostMessage
            {
                Type = "tabs",
                ActiveTabId = 2,
                Tabs = new[] { new TabInfo { Id = 2, Name = "a.md", Path = @"C:\a.md", Updated = true } },
            }.ToJson();

            Assert.Contains("\"activeTabId\":2", json);
            Assert.Contains("\"name\":\"a.md\"", json);
            Assert.Contains("\"updated\":true", json);
        }
    }
}
