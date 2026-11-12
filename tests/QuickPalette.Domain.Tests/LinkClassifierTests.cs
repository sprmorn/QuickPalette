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
    [InlineData("https://uuyc.163.com/invite/ed41f433ae79447d96bdb41fea90d3cb", "UU远程")]
    [InlineData("https://uuremote.163.com/xxx", "UU远程")]
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

    // ---------- 口令识别 ----------

    [Theory]
    [InlineData("腾讯会议：123-456-789")]
    [InlineData("会议号 123 456 789 请大家准时参加")]
    [InlineData("入会链接没发，会议号是 987654321")]
    public void Meeting_Code_Text_Classified(string text)
    {
        var result = LinkClassifier.Classify(text);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Meeting, result.Category);
        Assert.Equal("腾讯会议", result.TargetApp);
        Assert.StartsWith("wemeet://", result.LaunchUri);
    }

    [Fact]
    public void Bare_Digits_Not_Matched_Without_Context()
    {
        // 手机号/单号等裸数字串不能误报
        Assert.Null(LinkClassifier.Classify("我的电话是13812345678"));
        Assert.Null(LinkClassifier.Classify("订单号 2026111212345678"));
    }

    [Theory]
    [InlineData("ToDesk ID：123 456 789，密码：ab12")]
    [InlineData("todesk 设备码 987654321 密码 xyz9")]
    public void ToDesk_Code_Text_Classified(string text)
    {
        var result = LinkClassifier.Classify(text);

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Remote, result.Category);
        Assert.Equal("ToDesk", result.TargetApp);
        Assert.StartsWith("todesk://connect&", result.LaunchUri);
    }

    [Fact]
    public void AnyDesk_Code_Text_Classified()
    {
        var result = LinkClassifier.Classify("AnyDesk 地址：987 654 321");

        Assert.NotNull(result);
        Assert.Equal(LinkCategory.Remote, result.Category);
        Assert.Equal("AnyDesk", result.TargetApp);
    }

    [Theory]
    [InlineData("TeamViewer ID：987 654 321")]
    [InlineData("teamviewer 123456789")]
    public void TeamViewer_Code_Text_Classified(string text)
    {
        var result = LinkClassifier.Classify(text);

        Assert.NotNull(result);
        Assert.Equal("TeamViewer", result.TargetApp);
        Assert.StartsWith("teamviewer10://", result.LaunchUri);
    }

    [Fact]
    public void RustDesk_Code_Text_Classified()
    {
        var result = LinkClassifier.Classify("RustDesk：987654321");

        Assert.NotNull(result);
        Assert.Equal("RustDesk", result.TargetApp);
        Assert.Equal("rustdesk://987654321", result.LaunchUri);
    }

    [Fact]
    public void UU_Code_Text_Carries_Code_And_Password()
    {
        var result = LinkClassifier.Classify("UU远程 协助口令 123456 密码 ab12");

        Assert.NotNull(result);
        Assert.Equal("UU远程", result.TargetApp);
        Assert.Equal("uuremote://", result.LaunchUri); // 拉起客户端主界面
        Assert.Equal("123456", result.Code);
        Assert.Equal("ab12", result.Password);
    }

    [Fact]
    public void UU_Code_Without_Password_Ok()
    {
        var result = LinkClassifier.Classify("uu远程口令：AB12CD");

        Assert.NotNull(result);
        Assert.Equal("AB12CD", result.Code);
        Assert.Null(result.Password);
    }

    [Fact]
    public void UU_Official_Share_Format_Prefers_Code_Over_Invite_Link()
    {
        // 官方分享原文：链接 + 设备ID + 设备验证码混排 → 优先口令卡（可复制、拉起客户端）
        var text = """
                   【网易UU远程】完整复制本消息，打开 UU远程「开始协助」即可对我发起远程协助
                   设备ID：611211657
                   设备验证码：Z1R6U79M
                   点击链接可直接发起远程协助：https://uuyc.163.com/invite/ed41f433ae79447d96bdb41fea90d3cb
                   """;

        var result = LinkClassifier.Classify(text);

        Assert.NotNull(result);
        Assert.Equal("UU远程", result.TargetApp);
        Assert.Equal("uuremote://", result.LaunchUri);
        Assert.Equal("611211657", result.Code);
        Assert.Equal("Z1R6U79M", result.Password);
        Assert.Equal("设备ID", result.CodeLabel);
        Assert.Equal("验证码", result.PasswordLabel);
    }

    [Fact]
    public void UU_Invite_Link_Only_Still_Classified_As_UU()
    {
        var result = LinkClassifier.Classify("https://uuyc.163.com/invite/ed41f433ae79447d96bdb41fea90d3cb");

        Assert.NotNull(result);
        Assert.Equal("UU远程", result.TargetApp);
        Assert.Null(result.Code); // 只有链接时没有口令芯片
    }
}
