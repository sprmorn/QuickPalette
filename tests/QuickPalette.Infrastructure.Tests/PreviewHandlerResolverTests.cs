using QuickPalette.Infrastructure.Preview;
using Xunit;

namespace QuickPalette.Infrastructure.Tests;

/// <summary>
/// 预览处理器解析测试：扩展名 → CLSID 的三级关联链（直连 / ProgId / PerceivedType）、
/// 激活方式判定（LocalServer 任意位数可承载、进程内须位数一致）与 PE 架构探测。
/// 注册表经注入的假读取委托模拟，不触碰真实注册表。
/// </summary>
public class PreviewHandlerResolverTests
{
    private const string ShellExSuffix = "\\ShellEx\\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private static readonly Guid WordClsid = new("84F66100-FF7C-4FB4-B0C0-02CD7FB668FE");
    private static readonly Guid TxtClsid = new("1531D583-8375-4D3F-B5FB-D23BBD169F22");

    /// <summary>
    /// 构造注入假注册表的解析器。条目键为 HKCR 相对路径；命名值以 "::名称" 结尾，
    /// 不带 "::" 的是键默认值（如 ".md::PerceivedType" = "text"）。
    /// </summary>
    private static PreviewHandlerResolver Resolver(params (string Key, string? Value)[] entries)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries) map[key] = value;
        return new PreviewHandlerResolver((subKey, valueName) =>
        {
            var key = valueName is null ? subKey : $"{subKey}::{valueName}";
            return map.TryGetValue(key, out var value) ? value : null;
        });
    }

    // ───────────────────────── 关联链 ─────────────────────────

    [Fact]
    public void Find_扩展名直连关联_返回处理器注册()
    {
        var resolver = Resolver(
            ($".docx{ShellExSuffix}", WordClsid.ToString("B")),
            ($"CLSID\\{WordClsid:B}\\LocalServer32", "\"C:\\Office\\WINWORD.EXE\""));

        var registration = resolver.Find(".docx");

        Assert.NotNull(registration);
        Assert.Equal(WordClsid, registration.Clsid);
        Assert.Equal(PreviewHandlerActivationKind.LocalServer, registration.Kind);
    }

    [Fact]
    public void Find_扩展名大小写与有无点号_均等价()
    {
        var resolver = Resolver(
            ($".docx{ShellExSuffix}", WordClsid.ToString("B")),
            ($"CLSID\\{WordClsid:B}\\LocalServer32", "C:\\Office\\WINWORD.EXE"));

        Assert.NotNull(resolver.Find("DOCX"));
        Assert.NotNull(resolver.Find("docx"));
        Assert.NotNull(resolver.Find(".DoCx"));
    }

    [Fact]
    public void Find_经ProgId中转关联()
    {
        var resolver = Resolver(
            (".doc", "Word.Document.8"),
            ($"Word.Document.8{ShellExSuffix}", WordClsid.ToString("B")),
            ($"CLSID\\{WordClsid:B}\\LocalServer32", "C:\\Office\\WINWORD.EXE"));

        var registration = resolver.Find(".doc");

        Assert.NotNull(registration);
        Assert.Equal(WordClsid, registration.Clsid);
    }

    [Fact]
    public void Find_经PerceivedType中转关联_Txt类文件()
    {
        var resolver = Resolver(
            (".md::PerceivedType", "text"),
            ($"SystemFileAssociations\\text{ShellExSuffix}", TxtClsid.ToString("B")),
            ($"CLSID\\{TxtClsid:B}\\InprocServer32", "C:\\Windows\\System32\\shell32.dll"));

        var registration = resolver.Find(".md");

        Assert.NotNull(registration);
        Assert.Equal(TxtClsid, registration.Clsid);
        Assert.Equal(PreviewHandlerActivationKind.InProcServer, registration.Kind);
        Assert.Equal("C:\\Windows\\System32\\shell32.dll", registration.InProcServerPath);
    }

    [Fact]
    public void Find_三级均无关联_返回Null()
    {
        var resolver = Resolver(
            (".md", "text"),
            (".md::PerceivedType", "text"));

        Assert.Null(resolver.Find(".md"));
        Assert.Null(resolver.Find(".qqqq"));
    }

    [Fact]
    public void Find_空扩展名_返回Null()
    {
        var resolver = Resolver();

        Assert.Null(resolver.Find(null));
        Assert.Null(resolver.Find(""));
        Assert.Null(resolver.Find("   "));
    }

    [Fact]
    public void Find_32位视图注册的处理器_同样可解析()
    {
        var resolver = Resolver(
            ($".pdf{ShellExSuffix}", "{D7AC9243-9D84-4498-8192-D7AD263B63C8}"),
            ($"WOW6432Node\\CLSID\\{{D7AC9243-9D84-4498-8192-D7AD263B63C8}}\\InprocServer32",
                "C:\\wps\\pdfwspv.dll"));

        var registration = resolver.Find(".pdf");

        Assert.NotNull(registration);
        Assert.Equal(PreviewHandlerActivationKind.InProcServer, registration.Kind);
    }

    // ───────────────────────── 承载判定 ─────────────────────────

    [Fact]
    public void CanHost_LocalServer_任意位数宿主均可承载()
    {
        var registration = new PreviewHandlerRegistration(WordClsid, null, PreviewHandlerActivationKind.LocalServer, null, null);

        Assert.True(PreviewHandlerResolver.CanHost(registration, hostIs64Bit: true));
        Assert.True(PreviewHandlerResolver.CanHost(registration, hostIs64Bit: false));
    }

    [Fact]
    public void CanHost_进程内处理器_位数必须一致()
    {
        static PreviewHandlerRegistration InProc(string? arch) =>
            new(WordClsid, null, PreviewHandlerActivationKind.InProcServer, "x.dll", arch);

        Assert.True(PreviewHandlerResolver.CanHost(InProc("x64"), hostIs64Bit: true));
        Assert.False(PreviewHandlerResolver.CanHost(InProc("x86"), hostIs64Bit: true));
        Assert.False(PreviewHandlerResolver.CanHost(InProc("arm64"), hostIs64Bit: true));
        // 探测不出架构（文件不可读等）：放行试探，加载失败由回落链路兜底
        Assert.True(PreviewHandlerResolver.CanHost(InProc(null), hostIs64Bit: true));

        // x86 宿主的判定镜像对称（曾漏写导致 x86 宿主误拒全部 32 位处理器）
        Assert.True(PreviewHandlerResolver.CanHost(InProc("x86"), hostIs64Bit: false));
        Assert.False(PreviewHandlerResolver.CanHost(InProc("x64"), hostIs64Bit: false));
        Assert.False(PreviewHandlerResolver.CanHost(InProc("arm64"), hostIs64Bit: false));
        Assert.True(PreviewHandlerResolver.CanHost(InProc(null), hostIs64Bit: false));
    }

    [Fact]
    public void CanHost_未知注册方式_不承载()
    {
        var registration = new PreviewHandlerRegistration(WordClsid, null, PreviewHandlerActivationKind.Unknown, null, null);

        Assert.False(PreviewHandlerResolver.CanHost(registration));
    }

    [Fact]
    public void HostArch_LocalServer_任意宿主均可激活()
    {
        var registration = new PreviewHandlerRegistration(WordClsid, null, PreviewHandlerActivationKind.LocalServer, null, null);

        Assert.Equal(PreviewHandlerHostArch.Any, registration.HostArch);
    }

    [Fact]
    public void HostArch_进程内处理器_按DLL位数定向宿主()
    {
        static PreviewHandlerRegistration InProc(string? arch) =>
            new(WordClsid, null, PreviewHandlerActivationKind.InProcServer, "x.dll", arch);

        Assert.Equal(PreviewHandlerHostArch.X64Only, InProc("x64").HostArch);
        Assert.Equal(PreviewHandlerHostArch.X86Only, InProc("x86").HostArch);
        // 探测不出（含 arm64）：两种宿主都试，失败即回落
        Assert.Equal(PreviewHandlerHostArch.Any, InProc(null).HostArch);
    }

    // ───────────────────────── PE 架构探测 ─────────────────────────

    [Theory]
    [InlineData((ushort)0x8664, "x64")]
    [InlineData((ushort)0x014C, "x86")]
    [InlineData((ushort)0xAA64, "arm64")]
    public void ProbePeArch_识别PE机器码(ushort machine, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qp_pe_{machine:X4}_{Guid.NewGuid():N}.dll");
        try
        {
            var bytes = new byte[0x100];
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);          // e_lfanew
            BitConverter.GetBytes(0x0000_4550u).CopyTo(bytes, 0x80); // "PE\0\0"
            BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
            File.WriteAllBytes(path, bytes);

            Assert.Equal(expected, PreviewHandlerResolver.ProbePeArch(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbePeArch_非PE或不可读_返回Null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qp_pe_bad_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Null(PreviewHandlerResolver.ProbePeArch(path));
            Assert.Null(PreviewHandlerResolver.ProbePeArch(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
