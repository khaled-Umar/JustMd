using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        /// <summary>For <see cref="WebMessageType.EditorContent"/>.</summary>
        public int RequestId { get; internal init; }
    }

    /// <summary>Parses and validates JSON messages posted by the viewer page.</summary>
    public static class WebMessageParser
    {
        /// <summary>Upper bound for clipboard text coming from a code block.</summary>
        public const int MaxCopyLength = 16 * 1024 * 1024;

        /// <summary>Upper bound for a document coming from the editor, in characters.</summary>
        public const int MaxMarkdownLength = 8 * 1024 * 1024;

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

        /// <summary>render | welcome | error | tabs | theme | outline | toggleOutline | copied | scrollTo | selectAll | flushState | edit | exitEdit | editorCommand | requestContent | saved | documentRenamed | externalChange | imagePicked.</summary>
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

        /// <summary>Id the page echoes in "editorContent", for "requestContent".</summary>
        public int RequestId { get; set; }

        /// <summary>Markdown image reference for "imagePicked".</summary>
        public string? Src { get; set; }

        /// <summary>Image alternative text for "imagePicked".</summary>
        public string? Alt { get; set; }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
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
