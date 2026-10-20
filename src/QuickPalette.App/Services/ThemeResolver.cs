namespace QuickPalette.App.Services;

using Microsoft.Win32;

/// <summary>
/// 主题解析辅助：theme=system 时按系统实际生效主题（AppsUseLightTheme）解析为 dark/light。
/// </summary>
public static class ThemeResolver
{
    /// <summary>把 "dark"/"light"/"system" 解析为实际生效的 "dark"/"light"</summary>
    public static string Resolve(string? theme)
    {
        return theme?.ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => IsSystemDark() ? "dark" : "light"
        };
    }

    /// <summary>判断系统是否为深色模式（读取失败默认深色）</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int i || i == 0;
        }
        catch
        {
            return true;
        }
    }
}
