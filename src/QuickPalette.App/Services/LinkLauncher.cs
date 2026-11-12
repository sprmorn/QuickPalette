namespace QuickPalette.App.Services;

using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

/// <summary>
/// 链接跳转：http(s) 交给默认浏览器；自定义协议按注册表路由到已注册的客户端。
/// 未注册的协议视为"目标软件未安装"（调用方据此不弹提示）。
/// </summary>
public static class LinkLauncher
{
    /// <summary>该 URI 是否可跳转（http 恒真；自定义协议查 HKCR 注册）</summary>
    public static bool CanLaunch(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colon = uri.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var scheme = uri[..colon];
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>发起跳转（系统按协议路由到对应软件/浏览器）</summary>
    public static bool Launch(string uri, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            logger.Information("Link launched: {Uri}", uri);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Link launch failed: {Uri}", uri);
            return false;
        }
    }
}
