namespace QuickPalette.Domain.Tests;

using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using Xunit;

/// <summary>
/// PinyinProvider 内置码表行为测试
/// </summary>
public class PinyinProviderTests
{
    private readonly PinyinProvider _pinyin = PinyinProvider.Instance;

    [Fact]
    public void CommonWords_FullPinyin_And_Initials()
    {
        Assert.Equal("weixin", _pinyin.GetFullPinyin("微信"));
        Assert.Equal("wx", _pinyin.GetInitials("微信"));

        Assert.Equal("zhongguo", _pinyin.GetFullPinyin("中国"));
        Assert.Equal("zg", _pinyin.GetInitials("中国"));
    }

    [Fact]
    public void Polyphone_Generates_MultipleCandidates()
    {
        var item = new AppItem { Name = "重庆" };
        _pinyin.EnsurePinyin(item);

        // 多音字：chongqing 与 zhongqing 都应出现在候选里
        Assert.Contains("chongqing", item.Pinyin);
        Assert.Contains("zhongqing", item.Pinyin);
    }

    [Fact]
    public void Ascii_IsPreserved_Lowercased()
    {
        Assert.Equal("visual studio", _pinyin.GetFullPinyin("Visual Studio"));
        Assert.Equal("vs", _pinyin.GetInitials("VS"));
    }

    [Fact]
    public void Mixed_Text_KeepsAscii_And_TranslatesHanzi()
    {
        var full = _pinyin.GetFullPinyin("微信2024");
        Assert.Equal("weixin2024", full);
    }

    [Fact]
    public void UnknownChar_IsKept_AsIs()
    {
        // 表外生僻字不崩溃、原样保留
        Assert.Equal("龘a", _pinyin.GetFullPinyin("龘A"));
    }

    [Fact]
    public void EnsurePinyin_DoesNotOverwrite_ExistingValues()
    {
        var item = new AppItem { Name = "微信", Pinyin = "custom", PinyinInitials = "cx" };
        _pinyin.EnsurePinyin(item);

        Assert.Equal("custom", item.Pinyin);
        Assert.Equal("cx", item.PinyinInitials);
    }

    [Fact]
    public void EnsurePinyin_Uses_DisplayName_Too()
    {
        var item = new AppItem { Name = "微信", DisplayName = "WeChat" };
        _pinyin.EnsurePinyin(item);

        Assert.Contains("wechat", item.Pinyin);
    }
}
