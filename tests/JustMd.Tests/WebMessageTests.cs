using JustMd.Core.Messaging;

namespace JustMd.Tests
{
    public class WebMessageTests
    {
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
