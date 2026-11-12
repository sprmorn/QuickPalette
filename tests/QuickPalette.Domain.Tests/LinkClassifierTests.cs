namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Links;

/// <summary>链接助手识别器：类目优先级与链接提取</summary>
public class LinkClassifierTests
{
    [Theory]
    [InlineData("https://meeting.tencent.com/dm/abc123", LinkCategory.Meeting, "腾讯会议")]
    [InlineData("wemeet://page/inmeeting?meeting_code=123", LinkCategory.Meeting, "腾讯会议")]
    [InlineData("https://vc.feishu.cn/j/xyz", LinkCategory.Meeting, "飞书")]
    [InlineData("https://page.dingtalk.com/app/xxx", LinkCategory.Meeting, "钉钉")]
    [InlineData("https://work.weixin.qq.com/ca/xxx", LinkCategory.Meeting, "企业微信")]
    [InlineData("https://zoom.us/j/123456789", LinkCategory.Meeting, "Zoom")]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc", LinkCategory.Meeting, "Microsoft Teams")]
    public void Meeting_Links_Classified(string url, LinkCategory category, string app)
    {
        var result = LinkClassifier.ClassifyUrl(url);

        Assert.NotNull(result);
        Assert.Equal(category, result.Category);
        Assert.Equal(app, result.TargetApp);
    }

    [Theory]
    [InlineData("uuremote://external/device/control?anonymous_device_id=x", "UU远程")]
    [InlineData("todesk://remote?cid=123456789", "ToDesk")]
    [InlineData("https://get.teamviewer.com/xxxxxx", "TeamViewer")]
    [InlineData("anydesk://123456789", "AnyDesk")]
    [InlineData("rustdesk://192.168.1.2", "RustDesk")]
    [InlineData("sunlogin://remote?id=1", "向日葵")]
    public void Remote_Links_Classified(string url, string app)
    {
        var result = LinkClassifier.ClassifyUrl(url);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Remote, result.Category);
        Assert.Equal(app, result.TargetApp);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/1a2b3c?pwd=abcd", "百度网盘")]
    [InlineData("https://www.alipan.com/s/xxxx", "阿里云盘")]
    [InlineData("https://pan.quark.cn/s/xxxx", "夸克网盘")]
    [InlineData("https://www.123pan.com/s/xxxx", "123云盘")]
    [InlineData("https://115.com/s/xxxx", "115网盘")]
    [InlineData("https://cloud.189.cn/t/xxxx", "天翼云盘")]
    [InlineData("https://share.weiyun.com/xxxx", "微云")]
    public void CloudDisk_Links_Classified(string url, string app)
    {
        var result = LinkClassifier.ClassifyUrl(url);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.CloudDisk, result.Category);
        Assert.Equal(app, result.TargetApp);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("ed2k://file/test.mkv|123|abc|def|/")]
    [InlineData("thunder://QUFodHRwOi8vZXhhbXBsZS5jb20vWlo=")]
    [InlineData("https://example.com/movie.torrent")]
    [InlineData("https://example.com/archive.zip")]
    public void Download_Links_Classified(string url)
    {
        var result = LinkClassifier.ClassifyUrl(url);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Download, result.Category);
    }

    [Theory]
    [InlineData("https://music.163.com/#/song?id=12345", "网易云音乐")]
    [InlineData("orpheus://song/12345", "网易云音乐")]
    [InlineData("https://y.qq.com/n/ryqq/songDetail/abc", "QQ音乐")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "哔哩哔哩")]
    [InlineData("https://v.qq.com/x/cover/abc.html", "视频")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "视频")]
    public void Media_Links_Classified(string url, string app)
    {
        var result = LinkClassifier.ClassifyUrl(url);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Media, result.Category);
        Assert.Equal(app, result.TargetApp);
    }

    [Fact]
    public void Normal_Web_Link_Falls_To_Web()
    {
        var result = LinkClassifier.ClassifyUrl("https://github.com/sprmorn/QuickPalette");

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Web, result.Category);
    }

    [Fact]
    public void Extracts_Link_From_Mixed_Text()
    {
        var result = LinkClassifier.Classify("分享给你：https://pan.baidu.com/s/1a2b3c?pwd=abcd 提取码 abcd");

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.CloudDisk, result.Category);
    }

    [Fact]
    public void Plain_Text_Without_Link_Returns_Null()
    {
        Assert.Null(LinkClassifier.Classify("这是一段普通文字，没有任何链接。"));
        Assert.Null(LinkClassifier.Classify(string.Empty));
        Assert.Null(LinkClassifier.Classify(null));
    }

    [Fact]
    public void Priority_Meeting_Beats_Media_When_Multiple_Links()
    {
        var result = LinkClassifier.Classify(
            "先看这个视频 https://music.163.com/song?id=1，然后入会 https://meeting.tencent.com/dm/xyz");

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Meeting, result.Category);
    }
}
