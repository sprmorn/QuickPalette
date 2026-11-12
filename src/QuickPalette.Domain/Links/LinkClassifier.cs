namespace QuickPalette.Domain.Links;

using System.Text.RegularExpressions;

/// <summary>链接类目（优先级从高到低）</summary>
public enum LinkCategory
{
    /// <summary>会议办公：腾讯会议/飞书/钉钉/企业微信/Zoom/Teams</summary>
    Meeting,

    /// <summary>远程控制：UU远程/ToDesk/TeamViewer/AnyDesk/RustDesk/向日葵</summary>
    Remote,

    /// <summary>网盘：百度/阿里/夸克/123/115/天翼/微云</summary>
    CloudDisk,

    /// <summary>下载：磁力/电驴/迅雷链/资源直链</summary>
    Download,

    /// <summary>影音视频：音乐/视频站与音视频直链</summary>
    Media,

    /// <summary>网页：普通 http(s) 链接（兜底）</summary>
    Web,

    /// <summary>其他：QQ/邮件/电话等系统协议</summary>
    Other,
}

/// <summary>一条识别结果：类目 + 目标应用 + 实际跳转 URI + 展示信息</summary>
public sealed record LinkSuggestion(
    LinkCategory Category,
    string TargetApp,
    string LaunchUri,
    string DisplayText,
    string IconKey);

/// <summary>
/// 剪贴板链接识别器（纯逻辑，无副作用）：
/// 从文本中提取链接并按"会议 &gt; 远程 &gt; 网盘 &gt; 下载 &gt; 影音 &gt; 网页 &gt; 其他"优先级分类，
/// 返回首个匹配的建议。识别全部本地完成，不外传。
/// </summary>
public static partial class LinkClassifier
{
    /// <summary>链接提取（http/https 与常见自定义协议；含中文标点边界）</summary>
    [GeneratedRegex(@"(?:https?|magnet|ed2k|thunder|ftp|uuremote|wemeet|todesk|teamviewer|anydesk|rustdesk|sunlogin|dingtalk|feishu|lark|wxwork|zoommtg|msteams|qqmusic|orpheus|kugou|steam|tencent|mailto|tel|aliyundrive|baiduwp):[^\s""'<>\)\（\）。，；！？]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// <summary>识别文本中的链接；无可识别链接返回 null。
    /// 多条链接时按类目优先级（会议 &gt; 远程 &gt; 网盘 &gt; 下载 &gt; 影音 &gt; 网页 &gt; 其他）取最高的。</summary>
    public static LinkSuggestion? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        LinkSuggestion? best = null;
        foreach (Match match in LinkRegex().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', '（', '）', '。', '，', '；', '！', '？');
            var suggestion = ClassifyUrl(url);
            if (suggestion != null && (best == null || suggestion.Category < best.Category))
            {
                best = suggestion;
            }
        }

        return best;
    }

    /// <summary>对单条 URL 分类</summary>
    public static LinkSuggestion? ClassifyUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var lower = url.ToLowerInvariant();
        var host = ExtractHost(lower);

        // ---- 会议办公 ----
        if (lower.StartsWith("wemeet://") || host is "meeting.tencent.com" or "wemeet.qq.com")
        {
            return Make(LinkCategory.Meeting, "腾讯会议", url, "link-meeting");
        }

        if (lower.StartsWith("feishu://") || lower.StartsWith("lark://") || host.EndsWith(".feishu.cn") || host == "feishu.cn")
        {
            return Make(LinkCategory.Meeting, "飞书", url, "link-meeting");
        }

        if (lower.StartsWith("dingtalk://") || host.EndsWith(".dingtalk.com") || host == "dingtalk.com")
        {
            return Make(LinkCategory.Meeting, "钉钉", url, "link-meeting");
        }

        if (lower.StartsWith("wxwork://") || host.EndsWith(".weixin.qq.com") || host == "work.weixin.qq.com")
        {
            return Make(LinkCategory.Meeting, "企业微信", url, "link-meeting");
        }

        if (lower.StartsWith("zoommtg://") || host is "zoom.us" or "zoom.com.cn" or "www.zoom.us")
        {
            return Make(LinkCategory.Meeting, "Zoom", url, "link-meeting");
        }

        if (lower.StartsWith("msteams://") || host.EndsWith(".teams.microsoft.com") || host == "teams.microsoft.com")
        {
            return Make(LinkCategory.Meeting, "Microsoft Teams", url, "link-meeting");
        }

        // ---- 远程控制 ----
        if (lower.StartsWith("uuremote://"))
        {
            return Make(LinkCategory.Remote, "UU远程", url, "link-remote");
        }

        if (lower.StartsWith("todesk://"))
        {
            return Make(LinkCategory.Remote, "ToDesk", url, "link-remote");
        }

        if (lower.StartsWith("teamviewer://") || host.EndsWith(".teamviewer.com") || host == "teamviewer.com")
        {
            return Make(LinkCategory.Remote, "TeamViewer", url, "link-remote");
        }

        if (lower.StartsWith("anydesk://") || lower.StartsWith("anydesk:"))
        {
            return Make(LinkCategory.Remote, "AnyDesk", url, "link-remote");
        }

        if (lower.StartsWith("rustdesk://"))
        {
            return Make(LinkCategory.Remote, "RustDesk", url, "link-remote");
        }

        if (lower.StartsWith("sunlogin://"))
        {
            return Make(LinkCategory.Remote, "向日葵", url, "link-remote");
        }

        // ---- 网盘 ----
        if (host is "pan.baidu.com" or "yun.baidu.com" || lower.StartsWith("baiduwp://"))
        {
            return Make(LinkCategory.CloudDisk, "百度网盘", url, "link-cloud");
        }

        if (host is "alipan.com" or "www.alipan.com" or "aliyundrive.com" or "www.aliyundrive.com" || lower.StartsWith("aliyundrive://"))
        {
            return Make(LinkCategory.CloudDisk, "阿里云盘", url, "link-cloud");
        }

        if (host is "pan.quark.cn" or "drive.quark.cn")
        {
            return Make(LinkCategory.CloudDisk, "夸克网盘", url, "link-cloud");
        }

        if (host is "123pan.com" or "www.123pan.com" or "www.123865.com" or "123865.com")
        {
            return Make(LinkCategory.CloudDisk, "123云盘", url, "link-cloud");
        }

        if (host is "115.com" or "www.115.com" or "115cdn.com" or "115img.com" || lower.StartsWith("115://"))
        {
            return Make(LinkCategory.CloudDisk, "115网盘", url, "link-cloud");
        }

        if (host is "cloud.189.cn" or "cloud.189.com")
        {
            return Make(LinkCategory.CloudDisk, "天翼云盘", url, "link-cloud");
        }

        if (host.EndsWith("weiyun.com"))
        {
            return Make(LinkCategory.CloudDisk, "微云", url, "link-cloud");
        }

        // ---- 下载（磁力/电驴/迅雷/资源直链）----
        if (lower.StartsWith("magnet:"))
        {
            return Make(LinkCategory.Download, "迅雷", url, "link-download");
        }

        if (lower.StartsWith("ed2k://"))
        {
            return Make(LinkCategory.Download, "迅雷", url, "link-download");
        }

        if (lower.StartsWith("thunder://"))
        {
            return Make(LinkCategory.Download, "迅雷", url, "link-download");
        }

        if (lower.StartsWith("ftp://"))
        {
            return Make(LinkCategory.Download, "下载工具", url, "link-download");
        }

        // ---- 影音视频 ----
        if (lower.StartsWith("orpheus://") || host is "music.163.com" or "y.music.163.com")
        {
            return Make(LinkCategory.Media, "网易云音乐", url, "link-media");
        }

        if (lower.StartsWith("qqmusic://") || host.EndsWith(".qq.com") && host.Contains("y.qq"))
        {
            return Make(LinkCategory.Media, "QQ音乐", url, "link-media");
        }

        if (lower.StartsWith("kugou://") || host.EndsWith("kugou.com"))
        {
            return Make(LinkCategory.Media, "酷狗音乐", url, "link-media");
        }

        if (host.EndsWith("bilibili.com") || host == "b23.tv")
        {
            return Make(LinkCategory.Media, "哔哩哔哩", url, "link-media");
        }

        if (host.EndsWith("iqiyi.com") || host.EndsWith("youku.com") || host.EndsWith("v.qq.com") ||
            host.EndsWith("youtube.com") || host == "youtu.be")
        {
            return Make(LinkCategory.Media, "视频", url, "link-media");
        }

        // ---- 其他系统协议 ----
        if (lower.StartsWith("tencent://"))
        {
            return Make(LinkCategory.Other, "QQ", url, "link-mail");
        }

        if (lower.StartsWith("mailto:"))
        {
            return Make(LinkCategory.Other, "邮件", url, "link-mail");
        }

        if (lower.StartsWith("tel:"))
        {
            return Make(LinkCategory.Other, "电话", url, "link-mail");
        }

        // ---- http(s)：资源直链归下载，其余走网页兜底 ----
        if (lower.StartsWith("http://") || lower.StartsWith("https://"))
        {
            var path = ExtractPath(lower);
            if (path.EndsWithAnyOf(".torrent", ".zip", ".rar", ".7z", ".exe", ".msi", ".iso", ".dmg", ".apk", ".gz", ".bz2", ".xz"))
            {
                return Make(LinkCategory.Download, "迅雷", url, "link-download");
            }

            if (path.EndsWithAnyOf(".mp4", ".mkv", ".avi", ".rmvb", ".mov", ".flv", ".wmv", ".m3u8", ".ts", ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg"))
            {
                return Make(LinkCategory.Download, "迅雷", url, "link-download");
            }

            return Make(LinkCategory.Web, "浏览器", url, "link-web");
        }

        return null;
    }

    private static LinkSuggestion Make(LinkCategory category, string app, string uri, string iconKey)
        => new(category, app, uri, uri, iconKey);

    private static string ExtractHost(string lowerUrl)
    {
        try
        {
            // 协议相对/绝对 URL 都能取 host
            var normalized = lowerUrl.Contains("://") ? lowerUrl : $"http://{lowerUrl}";
            return new Uri(normalized).Host.ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractPath(string lowerUrl)
    {
        try
        {
            return new Uri(lowerUrl).AbsolutePath.ToLowerInvariant();
        }
        catch
        {
            return lowerUrl;
        }
    }
}

/// <summary>字符串小工具（避免引入额外依赖）</summary>
internal static class StringEndExtensions
{
    public static bool EndsWithAnyOf(this string value, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
