namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Links;

/// <summary>智能直跳：邀请页协议提取（纯解析）</summary>
public class PageSchemeExtractorTests
{
    [Fact]
    public void Extracts_Plain_Scheme_From_Html()
    {
        var html = """<html><a href="wemeet://page/inmeeting?meeting_code=123456789">加入会议</a></html>""";

        var result = PageSchemeExtractor.Extract(html);

        Assert.Equal("wemeet://page/inmeeting?meeting_code=123456789", result);
    }

    [Fact]
    public void Extracts_Js_Escaped_Scheme()
    {
        var html = """var url = "uuremote:\/\/external\/device\/control?anonymous_device_id=abc";""";

        Assert.Equal("uuremote://external/device/control?anonymous_device_id=abc", PageSchemeExtractor.Extract(html));
    }

    [Fact]
    public void Ignores_Unknown_Schemes_And_Ordinary_Urls()
    {
        Assert.Null(PageSchemeExtractor.Extract("""<a href="https://example.com/page">link</a>"""));
        Assert.Null(PageSchemeExtractor.Extract("""<a href="mailto:x@y.z">mail</a>"""));
        Assert.Null(PageSchemeExtractor.Extract(string.Empty));
        Assert.Null(PageSchemeExtractor.Extract(null));
    }

    [Fact]
    public void Prefers_Known_Client_Scheme_In_Busy_Page()
    {
        var html = """
                   <script>var a = "https://cdn.example.com/x.js";</script>
                   <script>var b = "aliyundrive://drive/backup";</script>
                   """;

        Assert.Equal("aliyundrive://drive/backup", PageSchemeExtractor.Extract(html));
    }
}
