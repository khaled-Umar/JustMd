using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using JustMd.Core.Tabs;

namespace JustMd.Core.Settings
{
    /// <summary>One remembered tab.</summary>
    public sealed class SessionTab
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>Document scroll position (CSS pixels) when the session was saved.</summary>
        [JsonPropertyName("scrollTop")]
        public double ScrollTop { get; set; }
    }

    /// <summary>Tabs that were open when the session was last saved, in order, plus the active one.</summary>
    public sealed class SessionState
    {
        public const int MaxTabs = 100;

        [JsonPropertyName("tabs")]
        public List<SessionTab> Tabs { get; set; } = new();

        [JsonPropertyName("activeIndex")]
        public int ActiveIndex { get; set; } = -1;

        public SessionState Clone() => new()
        {
            Tabs = Tabs.Select(t => new SessionTab { Path = t.Path, ScrollTop = t.ScrollTop }).ToList(),
            ActiveIndex = ActiveIndex,
        };

        public static bool AreEqual(SessionState? a, SessionState? b) =>
            ReferenceEquals(a, b)
            || (a != null && b != null && a.ActiveIndex == b.ActiveIndex && a.Tabs.Count == b.Tabs.Count
                && a.Tabs.Zip(b.Tabs).All(p => string.Equals(p.First.Path, p.Second.Path, StringComparison.OrdinalIgnoreCase)
                                               && p.First.ScrollTop.Equals(p.Second.ScrollTop)));
    }

    /// <summary>A tab to open at start-up.</summary>
    public sealed class StartupTab
    {
        public StartupTab(string path, double? scrollTop)
        {
            Path = path;
            ScrollTop = scrollTop;
        }

        public string Path { get; }

        /// <summary>Remembered scroll position for restored tabs; null for newly opened files.</summary>
        public double? ScrollTop { get; }
    }

    /// <summary>Start-up rule: restore the last session, then add the command-line files.</summary>
    public static class StartupPlan
    {
        /// <summary>
        /// Restored tabs come first (same order, missing or invalid files skipped, duplicates
        /// removed). Each argument file is then added as a tab unless it is already open. The
        /// last argument becomes active; without arguments the previously active tab does.
        /// Argument files are kept even if missing so the user sees an error tab for them.
        /// </summary>
        public static (IReadOnlyList<StartupTab> Tabs, int ActiveIndex) Build(
            SessionState? session, IReadOnlyList<string> arguments, Func<string, bool> fileExists)
        {
            var tabs = new List<StartupTab>();
            int active = -1;

            if (session != null)
            {
                for (int i = 0; i < session.Tabs.Count && tabs.Count < SessionState.MaxTabs; i++)
                {
                    string? path = DocumentPaths.Normalize(session.Tabs[i]?.Path);
                    if (path == null || !InstanceProtocol.IsAcceptablePath(path) || !fileExists(path) || IndexOf(tabs, path) >= 0)
                    {
                        continue;
                    }

                    if (i <= session.ActiveIndex)
                    {
                        active = tabs.Count;
                    }

                    double scroll = session.Tabs[i].ScrollTop;
                    tabs.Add(new StartupTab(path, double.IsFinite(scroll) && scroll > 0 ? scroll : null));
                }
            }

            foreach (string argument in arguments)
            {
                string? path = DocumentPaths.Normalize(argument);
                if (path == null || !InstanceProtocol.IsAcceptablePath(path))
                {
                    continue;
                }

                int existing = IndexOf(tabs, path);
                if (existing >= 0)
                {
                    active = existing;
                }
                else
                {
                    tabs.Add(new StartupTab(path, null));
                    active = tabs.Count - 1;
                }
            }

            if (tabs.Count == 0)
            {
                return (tabs, -1);
            }

            return (tabs, Math.Clamp(active, 0, tabs.Count - 1));
        }

        private static int IndexOf(List<StartupTab> tabs, string path) =>
            tabs.FindIndex(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
