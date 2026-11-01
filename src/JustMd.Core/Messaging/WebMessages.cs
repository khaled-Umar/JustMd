using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JustMd.Core.Spelling;
using JustMd.Core.Tabs;

namespace JustMd.Core.Messaging
{
    /// <summary>Message types the viewer page may send to the host. Anything else is ignored.</summary>
    public enum WebMessageType
    {
        /// <summary>The page has loaded and wants the current state.</summary>
        Ready,

        /// <summary>User asked to open a file (Open button).</summary>
        Open,

        /// <summary>Copy <see cref="WebMessage.Text"/> to the clipboard (code-block Copy button).</summary>
        Copy,

        /// <summary>User clicked a link; <see cref="WebMessage.Href"/> holds the raw href.</summary>
        Link,

        /// <summary>Cycle System, Light, Dark.</summary>
        CycleTheme,

        /// <summary>The outline panel was shown or hidden (<see cref="WebMessage.Visible"/>).</summary>
        Outline,

        /// <summary>Files were dropped on the page; paths arrive as additional objects.</summary>
        Drop,

        /// <summary>The outline panel was resized (<see cref="WebMessage.Width"/>, CSS pixels).</summary>
        OutlineWidth,

        /// <summary>User clicked a tab (<see cref="WebMessage.TabId"/>).</summary>
        ActivateTab,

        /// <summary>User closed a tab (close button or middle click).</summary>
        CloseTab,

        /// <summary>View state of a tab (scroll, outline) reported by the page.</summary>
        TabState,

        /// <summary>User asked for a new untitled tab.</summary>
        NewFile,

        /// <summary>User asked to edit a tab (<see cref="WebMessage.TabId"/>).</summary>
        EnterEdit,

        /// <summary>User asked to leave edit mode (Done or Esc).</summary>
        ExitEdit,

        /// <summary>User asked to save a tab.</summary>
        Save,

        /// <summary>User asked to save a tab under a new name.</summary>
        SaveAs,

        /// <summary>The editor's unsaved state changed (<see cref="WebMessage.Dirty"/>).</summary>
        EditorDirty,

        /// <summary>The answer to "requestContent" (<see cref="WebMessage.RequestId"/>, then <see cref="WebMessage.Markdown"/> and <see cref="WebMessage.Rtl"/>, or <see cref="WebMessage.Error"/>).</summary>
        EditorContent,

        /// <summary>User asked to browse for an image to insert.</summary>
        PickImage,

        /// <summary>User chose to discard their changes after the file changed on disk.</summary>
        ReloadFromDisk,

        /// <summary>Check the spelling of <see cref="WebMessage.Blocks"/>; answered with "spellResult".</summary>
        SpellCheck,

        /// <summary>Find replacements for <see cref="WebMessage.Word"/>; answered with "spellSuggestions".</summary>
        SpellSuggest,

        /// <summary>Stop flagging <see cref="WebMessage.Word"/> until JustMd closes.</summary>
        SpellIgnore,

        /// <summary>Add <see cref="WebMessage.Word"/> to the user's Windows dictionary.</summary>
        SpellAdd,
    }

    /// <summary>A validated message from the viewer page.</summary>
    public sealed class WebMessage
    {
        /// <summary>The document is over <see cref="WebMessageParser.MaxMarkdownLength"/>.</summary>
        public const string ErrorTooLarge = "tooLarge";

        /// <summary>The editor could not produce the Markdown.</summary>
        public const string ErrorFailed = "failed";

        internal WebMessage(WebMessageType type)
        {
            Type = type;
        }

        public WebMessageType Type { get; }

        public string? Text { get; internal init; }

        public string? Href { get; internal init; }

        public int Id { get; internal init; }

        public bool Visible { get; internal init; }

        public int Width { get; internal init; }

        public int TabId { get; internal init; }

        /// <summary>For <see cref="WebMessageType.TabState"/>.</summary>
        public TabViewState? View { get; internal init; }

        /// <summary>For <see cref="WebMessageType.EditorDirty"/>.</summary>
        public bool Dirty { get; internal init; }

        /// <summary>For <see cref="WebMessageType.EditorContent"/>.</summary>
        public bool Rtl { get; internal init; }

        /// <summary>For <see cref="WebMessageType.EditorContent"/>. Null when <see cref="Error"/> is set.</summary>
        public string? Markdown { get; internal init; }

        /// <summary>For <see cref="WebMessageType.EditorContent"/>: why the page has no Markdown to hand over (<see cref="ErrorTooLarge"/> or <see cref="ErrorFailed"/>), else null.</summary>
        public string? Error { get; internal init; }

        /// <summary>For <see cref="WebMessageType.EditorContent"/>, <see cref="WebMessageType.SpellCheck"/> and <see cref="WebMessageType.SpellSuggest"/>.</summary>
        public int RequestId { get; internal init; }

        /// <summary>For <see cref="WebMessageType.SpellCheck"/>: the page is scanning the whole document, not reporting an edit.</summary>
        public bool Scan { get; internal init; }

        /// <summary>For <see cref="WebMessageType.SpellCheck"/>.</summary>
        public IReadOnlyList<string>? Blocks { get; internal init; }

        /// <summary>For <see cref="WebMessageType.SpellSuggest"/>, <see cref="WebMessageType.SpellIgnore"/> and <see cref="WebMessageType.SpellAdd"/>.</summary>
        public string? Language { get; internal init; }

        /// <summary>For <see cref="WebMessageType.SpellSuggest"/>, <see cref="WebMessageType.SpellIgnore"/> and <see cref="WebMessageType.SpellAdd"/>.</summary>
        public string? Word { get; internal init; }
    }

    /// <summary>Parses and validates JSON messages posted by the viewer page.</summary>
    public static partial class WebMessageParser
    {
        /// <summary>Upper bound for clipboard text coming from a code block.</summary>
        public const int MaxCopyLength = 16 * 1024 * 1024;

        /// <summary>Upper bound for a document coming from the editor, in characters.</summary>
        public const int MaxMarkdownLength = 8 * 1024 * 1024;

        /// <summary>Upper bound for the blocks in one spelling request. Mirrored in editor.js.</summary>
        public const int MaxSpellingBlocks = 1000;

        /// <summary>Upper bound for one block in a spelling request, in characters. Mirrored in editor.js.</summary>
        public const int MaxSpellingBlockLength = 64 * 1024;

        /// <summary>Upper bound for all the blocks in one spelling request, in characters. Mirrored in editor.js.</summary>
        public const int MaxSpellingTextLength = 128 * 1024;

        /// <summary>Upper bound for a word sent for suggestions, ignoring or adding, in characters.</summary>
        public const int MaxSpellingWordLength = 128;

        /// <summary>Upper bound for the suggestions sent back for a word.</summary>
        public const int MaxSpellingSuggestions = 8;

        /// <summary>Returns the message, or null if the JSON is malformed, of an unknown type or fails validation.</summary>
        public static WebMessage? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxCopyLength * 2 || json[0] != '{')
            {
                return null;
            }

            IncomingDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<IncomingDto>(json);
            }
            catch (JsonException)
            {
                return null;
            }

            switch (dto?.Type)
            {
                case "ready":
                    return new WebMessage(WebMessageType.Ready);
                case "open":
                    return new WebMessage(WebMessageType.Open);
                case "cycleTheme":
                    return new WebMessage(WebMessageType.CycleTheme);
                case "drop":
                    return new WebMessage(WebMessageType.Drop);
                case "outline":
                    return dto.Visible is bool visible ? new WebMessage(WebMessageType.Outline) { Visible = visible } : null;
                case "outlineWidth":
                    return dto.Width is int width && width > 0 && width <= 10_000
                        ? new WebMessage(WebMessageType.OutlineWidth) { Width = width }
                        : null;
                case "activateTab":
                    return dto.TabId is int activateId && activateId > 0
                        ? new WebMessage(WebMessageType.ActivateTab) { TabId = activateId }
                        : null;
                case "closeTab":
                    return dto.TabId is int closeId && closeId > 0
                        ? new WebMessage(WebMessageType.CloseTab) { TabId = closeId }
                        : null;
                case "tabState":
                    return ParseTabState(dto);
                case "newFile":
                    return new WebMessage(WebMessageType.NewFile);
                case "enterEdit":
                    return ParseTabCommand(dto, WebMessageType.EnterEdit);
                case "exitEdit":
                    return ParseTabCommand(dto, WebMessageType.ExitEdit);
                case "save":
                    return ParseTabCommand(dto, WebMessageType.Save);
                case "saveAs":
                    return ParseTabCommand(dto, WebMessageType.SaveAs);
                case "pickImage":
                    return ParseTabCommand(dto, WebMessageType.PickImage);
                case "reloadFromDisk":
                    return ParseTabCommand(dto, WebMessageType.ReloadFromDisk);
                case "editorDirty":
                    return dto.TabId is int dirtyId && dirtyId > 0 && dto.Dirty is bool dirty
                        ? new WebMessage(WebMessageType.EditorDirty) { TabId = dirtyId, Dirty = dirty }
                        : null;
                case "editorContent":
                    return ParseEditorContent(dto);
                case "spellCheck":
                    return ParseSpellCheck(dto);
                case "spellSuggest":
                    return dto.RequestId is int suggestId && suggestId > 0
                        ? ParseSpellingWord(dto, WebMessageType.SpellSuggest, suggestId)
                        : null;
                case "spellIgnore":
                    return ParseSpellingWord(dto, WebMessageType.SpellIgnore, 0);
                case "spellAdd":
                    return ParseSpellingWord(dto, WebMessageType.SpellAdd, 0);
                case "copy":
                    return dto.Text == null || dto.Text.Length > MaxCopyLength
                        ? null
                        : new WebMessage(WebMessageType.Copy) { Text = dto.Text, Id = dto.Id ?? 0 };
                case "link":
                    return string.IsNullOrEmpty(dto.Href) || dto.Href.Length > LinkClassifier.MaxHrefLength
                        ? null
                        : new WebMessage(WebMessageType.Link) { Href = dto.Href };
                default:
                    return null;
            }
        }

        public const int MaxCollapsedKeys = 5000;
        public const int MaxCollapsedKeyLength = 4096;

        private static WebMessage? ParseTabCommand(IncomingDto dto, WebMessageType type) =>
            dto.TabId is int id && id > 0 ? new WebMessage(type) { TabId = id } : null;

        private static WebMessage? ParseEditorContent(IncomingDto dto)
        {
            if (dto.TabId is not int id || id <= 0
                || dto.RequestId is not int requestId || requestId <= 0)
            {
                return null;
            }

            if (dto.Error != null)
            {
                return dto.Error is WebMessage.ErrorTooLarge or WebMessage.ErrorFailed
                    ? new WebMessage(WebMessageType.EditorContent) { TabId = id, RequestId = requestId, Error = dto.Error }
                    : null;
            }

            if (dto.Rtl is not bool rtl
                || dto.Markdown == null || dto.Markdown.Length > MaxMarkdownLength)
            {
                return null;
            }

            return new WebMessage(WebMessageType.EditorContent) { TabId = id, RequestId = requestId, Rtl = rtl, Markdown = dto.Markdown };
        }

        private static WebMessage? ParseSpellCheck(IncomingDto dto)
        {
            if (dto.TabId is not int id || id <= 0
                || dto.RequestId is not int requestId || requestId <= 0
                || dto.Scan is not bool scan
                || dto.Blocks is not List<string?> blocks
                || blocks.Count == 0 || blocks.Count > MaxSpellingBlocks)
            {
                return null;
            }

            int total = 0;
            var checkedBlocks = new List<string>(blocks.Count);
            foreach (string? block in blocks)
            {
                if (block == null || block.Length > MaxSpellingBlockLength)
                {
                    return null;
                }

                total += block.Length;
                if (total > MaxSpellingTextLength)
                {
                    return null;
                }

                checkedBlocks.Add(block);
            }

            return new WebMessage(WebMessageType.SpellCheck) { TabId = id, RequestId = requestId, Scan = scan, Blocks = checkedBlocks };
        }

        private static WebMessage? ParseSpellingWord(IncomingDto dto, WebMessageType type, int requestId)
        {
            if (dto.TabId is not int id || id <= 0
                || dto.Language is not string language || !LanguageTag().IsMatch(language)
                || dto.Word is not string word || word.Length == 0 || word.Length > MaxSpellingWordLength
                || word.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return null;
            }

            return new WebMessage(type) { TabId = id, RequestId = requestId, Language = language, Word = word };
        }

        [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}\z", RegexOptions.CultureInvariant)]
        private static partial Regex LanguageTag();

        private static WebMessage? ParseTabState(IncomingDto dto)
        {
            if (dto.TabId is not int id || id <= 0)
            {
                return null;
            }

            double scroll = dto.ScrollTop ?? 0;
            double outlineScroll = dto.OutlineScrollTop ?? 0;
            if (!double.IsFinite(scroll) || !double.IsFinite(outlineScroll))
            {
                return null;
            }

            List<string> collapsed = dto.Collapsed ?? new List<string>();
            if (collapsed.Count > MaxCollapsedKeys || collapsed.Any(k => k == null || k.Length > MaxCollapsedKeyLength))
            {
                return null;
            }

            return new WebMessage(WebMessageType.TabState)
            {
                TabId = id,
                View = new TabViewState(Math.Max(0, scroll), Math.Max(0, outlineScroll), collapsed),
            };
        }

        private sealed class IncomingDto
        {
            [JsonPropertyName("tabId")]
            public int? TabId { get; set; }

            [JsonPropertyName("scrollTop")]
            public double? ScrollTop { get; set; }

            [JsonPropertyName("outlineScrollTop")]
            public double? OutlineScrollTop { get; set; }

            [JsonPropertyName("collapsed")]
            public List<string>? Collapsed { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("href")]
            public string? Href { get; set; }

            [JsonPropertyName("id")]
            public int? Id { get; set; }

            [JsonPropertyName("visible")]
            public bool? Visible { get; set; }

            [JsonPropertyName("width")]
            public int? Width { get; set; }

            [JsonPropertyName("dirty")]
            public bool? Dirty { get; set; }

            [JsonPropertyName("rtl")]
            public bool? Rtl { get; set; }

            [JsonPropertyName("markdown")]
            public string? Markdown { get; set; }

            [JsonPropertyName("requestId")]
            public int? RequestId { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("scan")]
            public bool? Scan { get; set; }

            [JsonPropertyName("blocks")]
            public List<string?>? Blocks { get; set; }

            [JsonPropertyName("language")]
            public string? Language { get; set; }

            [JsonPropertyName("word")]
            public string? Word { get; set; }
        }
    }

    /// <summary>A message from the host to the viewer page.</summary>
    public sealed class HostMessage
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>render | welcome | error | tabs | theme | outline | toggleOutline | copied | scrollTo | selectAll | flushState | edit | exitEdit | editorCommand | requestContent | saved | documentRenamed | externalChange | imagePicked | spellResult | spellSuggestions.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Rendered (unsanitised) document HTML for "render"; the page sanitises it.</summary>
        public string? Html { get; set; }

        public string? FileName { get; set; }

        public string? FilePath { get; set; }

        /// <summary>Image-host URL of the document folder, for relative raw-HTML images.</summary>
        public string? ImageBase { get; set; }

        public bool PreserveScroll { get; set; }

        public string? Fragment { get; set; }

        /// <summary>Effective theme: "light" or "dark".</summary>
        public string? Theme { get; set; }

        /// <summary>Selected theme mode: "system", "light" or "dark".</summary>
        public string? Mode { get; set; }

        public bool Visible { get; set; }

        /// <summary>Outline panel width for "outline".</summary>
        public int Width { get; set; }

        public int Id { get; set; }

        public bool Ok { get; set; }

        public string? Message { get; set; }

        /// <summary>Tab the "render"/"error" message belongs to.</summary>
        public int TabId { get; set; }

        /// <summary>Apply <see cref="ScrollTop"/>, <see cref="OutlineScrollTop"/> and <see cref="Collapsed"/> exactly.</summary>
        public bool RestoreView { get; set; }

        /// <summary>On "render": the file is above the editing size limit, so the edit button is disabled.</summary>
        public bool TooLarge { get; set; }

        public double ScrollTop { get; set; }

        public double OutlineScrollTop { get; set; }

        public IReadOnlyList<string>? Collapsed { get; set; }

        /// <summary>Tab strip for "tabs".</summary>
        public IReadOnlyList<TabInfo>? Tabs { get; set; }

        public int ActiveTabId { get; set; }

        /// <summary>The tab's edit revision, for "edit" and "saved".</summary>
        public int Revision { get; set; }

        /// <summary>Document body for "edit".</summary>
        public string? Markdown { get; set; }

        /// <summary>The document is right-to-left, for "edit".</summary>
        public bool Rtl { get; set; }

        /// <summary>The tab has no file yet, for "edit".</summary>
        public bool Untitled { get; set; }

        /// <summary>Command name for "editorCommand".</summary>
        public string? Command { get; set; }

        /// <summary>Command argument for "editorCommand", such as the heading level.</summary>
        public string? Arg { get; set; }

        /// <summary>Id the page echoes in "editorContent", for "requestContent"; the page's own id, for "spellResult" and "spellSuggestions".</summary>
        public int RequestId { get; set; }

        /// <summary>Markdown image reference for "imagePicked".</summary>
        public string? Src { get; set; }

        /// <summary>Image alternative text for "imagePicked".</summary>
        public string? Alt { get; set; }

        /// <summary>The page may ask for spell checks, for "edit".</summary>
        public bool Spelling { get; set; }

        /// <summary>The errors in each block of the request, in request order, for "spellResult".</summary>
        public IReadOnlyList<IReadOnlyList<SpellingErrorInfo>>? Blocks { get; set; }

        /// <summary>Replacements for the word, for "spellSuggestions". Empty when there are none.</summary>
        public IReadOnlyList<string>? Suggestions { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>One misspelling in a block of a "spellResult", in UTF-16 units of the block's text.</summary>
    public sealed class SpellingErrorInfo
    {
        // The message options drop default values, but the first word of a block starts at 0.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int Start { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int Length { get; set; }

        public string Language { get; set; } = string.Empty;

        /// <summary>The Windows CORRECTIVE_ACTION: 1 suggestions, 2 replace with <see cref="Replacement"/>, 3 delete.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int Action { get; set; }

        public string? Replacement { get; set; }

        public static SpellingErrorInfo From(SpellingError error) => new()
        {
            Start = error.Start,
            Length = error.Length,
            Language = error.Language,
            Action = error.Action,
            Replacement = error.Replacement,
        };
    }

    /// <summary>One entry in the tab strip.</summary>
    public sealed class TabInfo
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public bool Updated { get; set; }

        public bool Dirty { get; set; }

        public bool Editing { get; set; }

        public bool Untitled { get; set; }
    }
}
