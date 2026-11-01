using JustMd.Core.Messaging;

namespace JustMd.Tests
{
    public class WebMessageTests
    {
        [Fact]
        public void Editor_content_with_a_lone_surrogate_is_rejected_without_throwing()
        {
            // JSON.stringify writes a lone surrogate as an escape, which is valid JSON.
            string json = "{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"rtl\":false,\"markdown\":\"a\\ud83d b\"}";

            WebMessage? message = WebMessageParser.Parse(json);

            Assert.Null(message);
        }

        [Fact]
        public void Parses_known_messages()
        {
            Assert.Equal(WebMessageType.Ready, WebMessageParser.Parse("{\"type\":\"ready\"}")!.Type);
            Assert.Equal(WebMessageType.Open, WebMessageParser.Parse("{\"type\":\"open\"}")!.Type);
            Assert.Equal(WebMessageType.CycleTheme, WebMessageParser.Parse("{\"type\":\"cycleTheme\"}")!.Type);
            Assert.Equal(WebMessageType.Drop, WebMessageParser.Parse("{\"type\":\"drop\"}")!.Type);

            WebMessage copy = WebMessageParser.Parse("{\"type\":\"copy\",\"id\":7,\"text\":\"a\\nb\"}")!;
            Assert.Equal(WebMessageType.Copy, copy.Type);
            Assert.Equal(7, copy.Id);
            Assert.Equal("a\nb", copy.Text);

            WebMessage link = WebMessageParser.Parse("{\"type\":\"link\",\"href\":\"https://example.com\"}")!;
            Assert.Equal("https://example.com", link.Href);

            WebMessage outline = WebMessageParser.Parse("{\"type\":\"outline\",\"visible\":false}")!;
            Assert.Equal(WebMessageType.Outline, outline.Type);
            Assert.False(outline.Visible);

            WebMessage width = WebMessageParser.Parse("{\"type\":\"outlineWidth\",\"width\":300}")!;
            Assert.Equal(WebMessageType.OutlineWidth, width.Type);
            Assert.Equal(300, width.Width);
        }

        [Theory]
        [InlineData("{\"type\":\"outlineWidth\"}")]
        [InlineData("{\"type\":\"outlineWidth\",\"width\":0}")]
        [InlineData("{\"type\":\"outlineWidth\",\"width\":-5}")]
        [InlineData("{\"type\":\"outlineWidth\",\"width\":99999}")]
        [InlineData("{\"type\":\"outlineWidth\",\"width\":12.5}")]
        public void Rejects_invalid_outline_width(string json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("\"ready\"")]
        [InlineData("{}")]
        [InlineData("{\"type\":\"exec\"}")]
        [InlineData("{\"type\":\"READY\"}")]
        [InlineData("{\"type\":5}")]
        [InlineData("{\"type\":\"link\"}")]
        [InlineData("{\"type\":\"link\",\"href\":\"\"}")]
        [InlineData("{\"type\":\"copy\"}")]
        [InlineData("{\"type\":\"copy\",\"text\":\"x\",\"id\":\"7\"}")]
        [InlineData("{\"type\":\"outline\"}")]
        [InlineData("{not json")]
        public void Rejects_unknown_or_invalid_messages(string? json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Fact]
        public void Rejects_overlong_href()
        {
            string json = "{\"type\":\"link\",\"href\":\"https://e.com/" + new string('a', 9000) + "\"}";

            Assert.Null(WebMessageParser.Parse(json));
        }

        [Fact]
        public void Parses_new_file_without_a_tab()
        {
            Assert.Equal(WebMessageType.NewFile, WebMessageParser.Parse("{\"type\":\"newFile\"}")!.Type);
        }

        [Theory]
        [InlineData("enterEdit", WebMessageType.EnterEdit)]
        [InlineData("exitEdit", WebMessageType.ExitEdit)]
        [InlineData("save", WebMessageType.Save)]
        [InlineData("saveAs", WebMessageType.SaveAs)]
        [InlineData("pickImage", WebMessageType.PickImage)]
        [InlineData("reloadFromDisk", WebMessageType.ReloadFromDisk)]
        public void Parses_tab_commands_and_requires_a_valid_tab(string type, WebMessageType expected)
        {
            WebMessage message = WebMessageParser.Parse("{\"type\":\"" + type + "\",\"tabId\":4}")!;

            Assert.Equal(expected, message.Type);
            Assert.Equal(4, message.TabId);

            Assert.Null(WebMessageParser.Parse("{\"type\":\"" + type + "\"}"));
            Assert.Null(WebMessageParser.Parse("{\"type\":\"" + type + "\",\"tabId\":0}"));
            Assert.Null(WebMessageParser.Parse("{\"type\":\"" + type + "\",\"tabId\":-1}"));
            Assert.Null(WebMessageParser.Parse("{\"type\":\"" + type + "\",\"tabId\":\"4\"}"));
        }

        [Fact]
        public void Parses_editor_dirty()
        {
            WebMessage message = WebMessageParser.Parse("{\"type\":\"editorDirty\",\"tabId\":2,\"dirty\":true}")!;

            Assert.Equal(WebMessageType.EditorDirty, message.Type);
            Assert.Equal(2, message.TabId);
            Assert.True(message.Dirty);
            Assert.False(WebMessageParser.Parse("{\"type\":\"editorDirty\",\"tabId\":2,\"dirty\":false}")!.Dirty);
        }

        [Theory]
        [InlineData("{\"type\":\"editorDirty\",\"dirty\":true}")]
        [InlineData("{\"type\":\"editorDirty\",\"tabId\":0,\"dirty\":true}")]
        [InlineData("{\"type\":\"editorDirty\",\"tabId\":1}")]
        [InlineData("{\"type\":\"editorDirty\",\"tabId\":1,\"dirty\":\"true\"}")]
        [InlineData("{\"type\":\"editorDirty\",\"tabId\":1,\"dirty\":1}")]
        [InlineData("{\"type\":\"editorDirty\",\"tabId\":1,\"dirty\":null}")]
        public void Rejects_invalid_editor_dirty(string json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Fact]
        public void Parses_editor_content()
        {
            WebMessage message = WebMessageParser.Parse(
                "{\"type\":\"editorContent\",\"tabId\":2,\"requestId\":9,\"markdown\":\"# T\\n\",\"rtl\":true}")!;

            Assert.Equal(WebMessageType.EditorContent, message.Type);
            Assert.Equal(2, message.TabId);
            Assert.Equal(9, message.RequestId);
            Assert.Equal("# T\n", message.Markdown);
            Assert.True(message.Rtl);
        }

        [Fact]
        public void Empty_markdown_is_valid_editor_content()
        {
            WebMessage message = WebMessageParser.Parse("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"markdown\":\"\",\"rtl\":false}")!;

            Assert.Equal(string.Empty, message.Markdown);
        }

        [Theory]
        [InlineData("{\"type\":\"editorContent\",\"requestId\":1,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":0,\"requestId\":1,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":0,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":-2,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1.5,\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"markdown\":null,\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"markdown\":5,\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"markdown\":\"a\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"markdown\":\"a\",\"rtl\":\"yes\"}")]
        public void Rejects_invalid_editor_content(string json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Theory]
        [InlineData("tooLarge")]
        [InlineData("failed")]
        public void Editor_content_can_report_an_error_instead_of_markdown(string error)
        {
            WebMessage message = WebMessageParser.Parse(
                "{\"type\":\"editorContent\",\"tabId\":2,\"requestId\":9,\"error\":\"" + error + "\"}")!;

            Assert.Equal(WebMessageType.EditorContent, message.Type);
            Assert.Equal(2, message.TabId);
            Assert.Equal(9, message.RequestId);
            Assert.Equal(error, message.Error);
            Assert.Null(message.Markdown);
        }

        [Fact]
        public void An_error_wins_over_markdown_sent_with_it()
        {
            WebMessage message = WebMessageParser.Parse(
                "{\"type\":\"editorContent\",\"tabId\":2,\"requestId\":9,\"error\":\"failed\",\"markdown\":\"a\",\"rtl\":true}")!;

            Assert.Equal("failed", message.Error);
            Assert.Null(message.Markdown);
            Assert.False(message.Rtl);
        }

        [Fact]
        public void Valid_editor_content_has_no_error()
        {
            WebMessage message = WebMessageParser.Parse(
                "{\"type\":\"editorContent\",\"tabId\":2,\"requestId\":9,\"markdown\":\"a\",\"rtl\":false}")!;

            Assert.Null(message.Error);
            Assert.Equal("a", message.Markdown);
        }

        [Theory]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"error\":\"other\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"error\":\"TOOLARGE\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"error\":\"\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"error\":5}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"error\":\"other\",\"markdown\":\"a\",\"rtl\":true}")]
        [InlineData("{\"type\":\"editorContent\",\"requestId\":1,\"error\":\"failed\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":0,\"requestId\":1,\"error\":\"failed\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"error\":\"tooLarge\"}")]
        [InlineData("{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":0,\"error\":\"tooLarge\"}")]
        public void Rejects_unknown_editor_errors_and_errors_without_ids(string json)
        {
            Assert.Null(WebMessageParser.Parse(json));
        }

        [Fact]
        public void Editor_content_length_limit_is_inclusive()
        {
            static string Json(int length) =>
                "{\"type\":\"editorContent\",\"tabId\":1,\"requestId\":1,\"rtl\":false,\"markdown\":\"" + new string('a', length) + "\"}";

            Assert.Equal(8 * 1024 * 1024, WebMessageParser.MaxMarkdownLength);
            Assert.NotNull(WebMessageParser.Parse(Json(WebMessageParser.MaxMarkdownLength)));
            Assert.Null(WebMessageParser.Parse(Json(WebMessageParser.MaxMarkdownLength + 1)));
        }

        [Fact]
        public void Edit_message_serialises_camel_case()
        {
            string json = new HostMessage
            {
                Type = "edit",
                TabId = 3,
                Revision = 2,
                Markdown = "# T",
                Rtl = true,
                Untitled = true,
            }.ToJson();

            Assert.Equal("{\"type\":\"edit\",\"tabId\":3,\"revision\":2,\"markdown\":\"# T\",\"rtl\":true,\"untitled\":true}", json);
        }

        [Fact]
        public void Editor_host_messages_serialise_their_fields()
        {
            Assert.Equal(
                "{\"type\":\"editorCommand\",\"command\":\"setHeading\",\"arg\":\"2\"}",
                new HostMessage { Type = "editorCommand", Command = "setHeading", Arg = "2" }.ToJson());
            Assert.Equal(
                "{\"type\":\"requestContent\",\"tabId\":1,\"requestId\":7}",
                new HostMessage { Type = "requestContent", TabId = 1, RequestId = 7 }.ToJson());
            Assert.Equal(
                "{\"type\":\"imagePicked\",\"tabId\":1,\"src\":\"a.png\",\"alt\":\"a\"}",
                new HostMessage { Type = "imagePicked", TabId = 1, Src = "a.png", Alt = "a" }.ToJson());
        }

        [Fact]
        public void Empty_markdown_is_still_sent()
        {
            Assert.Contains("\"markdown\":\"\"", new HostMessage { Type = "edit", Markdown = string.Empty }.ToJson());
        }

        [Fact]
        public void Tab_info_serialises_new_flags_and_omits_defaults()
        {
            string json = new HostMessage
            {
                Type = "tabs",
                Tabs = new[]
                {
                    new TabInfo { Id = 1, Name = "Untitled 1", Dirty = true, Editing = true, Untitled = true },
                    new TabInfo { Id = 2, Name = "a.md" },
                },
            }.ToJson();

            Assert.Contains("\"dirty\":true,\"editing\":true,\"untitled\":true", json);
            Assert.Contains("{\"id\":2,\"name\":\"a.md\",\"path\":\"\"}", json);
        }

        [Fact]
        public void Host_message_serialises_compactly()
        {
            string json = new HostMessage { Type = "copied", Id = 3, Ok = true }.ToJson();

            Assert.Equal("{\"type\":\"copied\",\"id\":3,\"ok\":true}", json);
        }

        [Fact]
        public void Host_message_escapes_html_safely()
        {
            string json = new HostMessage { Type = "render", Html = "<script>\"x\"</script>" }.ToJson();

            Assert.DoesNotContain("<script>", json);
            Assert.Contains("\"html\":", json);
        }
    }
}
