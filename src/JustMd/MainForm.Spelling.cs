using System;
using System.Collections.Generic;
using System.Linq;
using JustMd.Core.Messaging;
using JustMd.Core.Spelling;
using JustMd.Core.Tabs;

namespace JustMd
{
    /// <summary>Spell checking for the editor: the page asks, the Windows checker answers on its own thread.</summary>
    internal sealed partial class MainForm
    {
        private void HandleSpellingMessage(WebMessage message)
        {
            if (!IsEditingTab(message.TabId))
            {
                return;
            }

            int tabId = message.TabId;
            int requestId = message.RequestId;
            switch (message.Type)
            {
                case WebMessageType.SpellCheck:
                    _spelling.Check(tabId, message.Scan, message.Blocks ?? Array.Empty<string>(), results =>
                    {
                        if (IsEditingTab(tabId))
                        {
                            Post(new HostMessage { Type = "spellResult", TabId = tabId, RequestId = requestId, Blocks = ToBlocks(results) });
                        }
                    });
                    break;

                case WebMessageType.SpellSuggest:
                    _spelling.Suggest(tabId, message.Language ?? string.Empty, message.Word ?? string.Empty, suggestions =>
                    {
                        if (IsEditingTab(tabId))
                        {
                            Post(new HostMessage { Type = "spellSuggestions", TabId = tabId, RequestId = requestId, Suggestions = suggestions });
                        }
                    });
                    break;

                case WebMessageType.SpellIgnore:
                    _spelling.Ignore(message.Language ?? string.Empty, message.Word ?? string.Empty);
                    break;

                case WebMessageType.SpellAdd:
                    _spelling.Add(message.Language ?? string.Empty, message.Word ?? string.Empty);
                    break;
            }
        }

        private bool IsEditingTab(int tabId) => _tabs.FindById(tabId) is DocumentTab { IsEditing: true };

        private static List<IReadOnlyList<SpellingErrorInfo>> ToBlocks(IReadOnlyList<IReadOnlyList<SpellingError>> results) =>
            results.Select(block => (IReadOnlyList<SpellingErrorInfo>)block.Select(SpellingErrorInfo.From).ToList()).ToList();

        /// <summary>Called from the spelling thread.</summary>
        private void RunOnUiThread(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Window is closing.
            }
        }
    }
}
