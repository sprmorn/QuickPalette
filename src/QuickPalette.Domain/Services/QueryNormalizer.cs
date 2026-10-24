namespace QuickPalette.Domain.Services;

using System.Text;

/// <summary>
/// 查询/名称归一化：Trim + 小写 + 全角转半角（ＡＢＣ１２３→abc123，全角空格→空格）。
/// 搜索引擎与系统组件搜索共用，保证匹配口径一致。
/// </summary>
public static class QueryNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return string.Empty;

        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (ch >= '\uFF01' && ch <= '\uFF5E')
                sb.Append((char)(ch - 0xFEE0)); // 全角 ASCII → 半角
            else if (ch == '\u3000')
                sb.Append(' ');
            else
                sb.Append(ch);
        }

        return sb.ToString().ToLowerInvariant();
    }
}
