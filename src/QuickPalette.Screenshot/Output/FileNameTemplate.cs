namespace QuickPalette.Screenshot.Output;

using System.Globalization;

/// <summary>
/// 截图文件命名模板：支持 yyyy / MM / dd / HH / mm / ss 占位符，
/// 非法文件名字符自动替换为下划线；空模板回退默认名。
/// </summary>
public static class FileNameTemplate
{
    public const string DefaultTemplate = "截图_yyyyMMdd_HHmmss";

    /// <summary>按模板生成文件名（不含扩展名）</summary>
    public static string Build(string? template, DateTime timestamp)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();

        var inv = CultureInfo.InvariantCulture;
        var name = t
            .Replace("yyyy", timestamp.Year.ToString("D4", inv))
            .Replace("MM", timestamp.Month.ToString("D2", inv))
            .Replace("dd", timestamp.Day.ToString("D2", inv))
            .Replace("HH", timestamp.Hour.ToString("D2", inv))
            .Replace("mm", timestamp.Minute.ToString("D2", inv))
            .Replace("ss", timestamp.Second.ToString("D2", inv));

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim().TrimEnd('.');

        // 模板全是非法字符（会产出 "____" 之类）→ 回退默认名
        return name.Trim(' ', '.', '_').Length == 0 ? BuildDefault(timestamp) : name;
    }

    /// <summary>默认文件名：截图_yyyyMMdd_HHmmss</summary>
    public static string BuildDefault(DateTime timestamp)
        => FormattableString.Invariant($"截图_{timestamp:yyyyMMdd_HHmmss}");
}
