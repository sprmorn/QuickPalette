namespace QuickPalette.Domain.Links;

using System.Text.RegularExpressions;

/// <summary>
/// 邀请页协议嗅探（纯解析）：从网页 HTML/脚本中提取真实客户端协议 URI（如 wemeet://、uuremote://）。
/// 只认白名单协议，避免把普通 URL 误当协议。
/// </summary>
public static partial class PageSchemeExtractor
{
    /// <summary>已知客户端协议白名单</summary>
    private static readonly string[] KnownSchemes =
    {
        "uuremote://", "wemeet://", "aliyundrive://", "baiduwp://", "dingtalk://", "feishu://", "lark://",
        "wxwork://", "zoommtg://", "msteams://", "todesk://", "sunlogin://", "teamviewer", "anydesk:",
        "rustdesk://", "orpheus://", "qqmusic://", "kugou://", "steam://"
    };

    /// <summary>任意协议 URI 提取</summary>
    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9+.\-]+://[^\s""'<>\\\)]+", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    /// <summary>从页面内容中提取首个已知客户端协议 URI；未命中返回 null</summary>
    public static string? Extract(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        // 常见脚本转义还原：JS 的 \/、\u002F、\u0026 与 HTML 的 &amp;
        var text = html
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.Ordinal);

        foreach (Match match in SchemeRegex().Matches(text))
        {
            var uri = match.Value.TrimEnd('\\', ')', '，', '。', ';', '"', '\'');
            foreach (var scheme in KnownSchemes)
            {
                if (uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return uri;
                }
            }
        }

        return null;
    }
}
