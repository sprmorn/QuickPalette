namespace QuickPalette.App.Services;

using System.Net.Http;
using System.Text;
using QuickPalette.Domain.Links;
using Serilog;

/// <summary>
/// 智能直跳：轻量嗅探邀请页（限时 1.5s、最多读 256KB），提取真实客户端协议；
/// 超时/失败/未命中返回 null，调用方回退浏览器打开原链接。
/// 手动跟随 http 重定向：302 直接跳协议（Location: wemeet://…）也算命中。
/// </summary>
public static class SmartLinkResolver
{
    public const int DefaultTimeoutMs = 1500;
    private const int MaxRedirects = 5;
    private const int MaxBytes = 256 * 1024;

    public static async Task<string?> ResolveAsync(string url, ILogger logger, int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false, // 手动跟随：302 指向协议时直接捕获
                UseCookies = false
            });
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QuickPalette/1.7 (link-assistant)");

            var current = url;
            for (var i = 0; i <= MaxRedirects; i++)
            {
                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                var location = response.Headers.Location;
                if (location != null)
                {
                    var target = location.IsAbsoluteUri
                        ? location.AbsoluteUri
                        : new Uri(new Uri(current), location).ToString();
                    if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Information("Smart jump: redirect hits scheme {Uri}", target);
                        return target;
                    }

                    current = target;
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[MaxBytes];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token)) > 0)
                {
                    total += read;
                }

                var html = Encoding.UTF8.GetString(buffer, 0, total);
                var scheme = PageSchemeExtractor.Extract(html);
                if (scheme != null)
                {
                    logger.Information("Smart jump: scheme found in page {Uri}", scheme);
                }

                return scheme;
            }
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Smart jump resolve failed (fallback to browser): {Url}", url);
        }

        return null;
    }
}
