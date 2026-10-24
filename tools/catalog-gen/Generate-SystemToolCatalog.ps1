# 从 windows-components.json 生成 SystemToolCatalog.cs
# 规则：JSON 为基底（实测校验），去掉与设置页重复的经典入口，重名消歧，补 12 条独有条目。
$ErrorActionPreference = 'Stop'
# 仓库根目录 = 本脚本所在 tools\catalog-gen 的上两级
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$items = Get-Content (Join-Path $root 'tools\catalog-gen\windows-components.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# ---- 删除：与设置页/文件夹重复的经典入口（Windows 10/11 上会重定向或等价） ----
$dropLaunch = @(
    'desk.cpl',                                        # 与 ms-settings:display 重复
    'control.exe /name Microsoft.Fonts',               # 与 shell:Fonts 重复
    'control.exe /name Microsoft.AutoPlay',            # 与 ms-settings:autoplay 重复
    'control.exe /name Microsoft.Troubleshooting',     # 与 ms-settings:troubleshoot 重复
    'odbcad32'                                         # 与补充的 64 位条目同命令，保留 64 位命名
)

# ---- 重名消歧（键 = zh|cat） ----
$rename = @{
    '声音|control-panel'    = '声音（经典）'
    '区域|control-panel'    = '区域（经典）'
    '日期和时间|control-panel' = '日期和时间（经典）'
    '恢复|control-panel'    = '恢复（控制面板）'
    '文档|settings'         = '文档（隐私）'
    '图片|settings'         = '图片（隐私）'
    '视频|settings'         = '视频（隐私）'
    '字体|shell-folder'     = '字体文件夹'
    '搜索|shell-folder'     = '搜索文件夹'
}

# ---- 补充条目（JSON 未收录，含经实测修正的 admin 标记） ----
$extras = @(
    @{cat='system-tool';   zh='PowerShell 7'; en='PowerShell 7'; launch='pwsh'; admin=$false; kw=@('pwsh','powershell7')},
    @{cat='system-tool';   zh='Windows 内存诊断'; en='Windows Memory Diagnostic'; launch='mdsched'; admin=$true; kw=@('内存诊断','内存检测','mdsched')},
    @{cat='system-tool';   zh='文件签名验证工具'; en='File Signature Verification'; launch='sigverif'; admin=$false; kw=@('签名验证','sigverif')},
    @{cat='system-tool';   zh='创建共享文件夹向导'; en='Create Shared Folder Wizard'; launch='shrpubw'; admin=$false; kw=@('共享向导','shrpubw')},
    @{cat='system-tool';   zh='IExpress 向导'; en='IExpress Wizard'; launch='iexpress'; admin=$false; kw=@('自解压','打包','iexpress')},
    @{cat='system-tool';   zh='驱动程序验证程序管理器'; en='Driver Verifier Manager'; launch='verifier'; admin=$true; kw=@('驱动验证','verifier')},
    @{cat='system-tool';   zh='Windows 传真和扫描'; en='Windows Fax and Scan'; launch='WFS'; admin=$false; kw=@('传真','扫描','wfs')},
    @{cat='system-tool';   zh='添加设备'; en='Add a device'; launch='devicepairingwizard'; admin=$false; kw=@('添加设备','设备配对','devicepairingwizard')},
    @{cat='system-tool';   zh='录音机'; en='Sound Recorder'; launch='explorer.exe shell:AppsFolder\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe!App'; admin=$false; kw=@('录音','录音机','soundrecorder')},
    @{cat='system-tool';   zh='Windows 光盘映像刻录机'; en='Windows Disc Image Burner'; launch='isoburn'; admin=$false; kw=@('iso','刻录','isoburn')},
    @{cat='system-tool';   zh='音量合成器（经典）'; en='Volume Mixer (Classic)'; launch='sndvol'; admin=$false; kw=@('单独音量','混音器','sndvol')},
    @{cat='system-tool';   zh='ODBC 数据源(64 位)'; en='ODBC Data Sources (64-bit)'; launch='odbcad32'; admin=$true; kw=@('odbc','数据源','数据库','odbcad32')}
)

# ---- 组装 ----
$final = New-Object System.Collections.Generic.List[object]
foreach ($e in $items) {
    if ($dropLaunch -contains $e.launch) { continue }
    $key = "$($e.zh)|$($e.cat)"
    if ($rename.ContainsKey($key)) { $e.zh = $rename[$key] }
    if ($e.cat -eq 'shell-folder' -and $e.zh -eq '控制面板') { continue }  # 与控制面板入口重复
    $final.Add($e)
}
foreach ($x in $extras) { $final.Add([pscustomobject]$x) }

# ---- 名称唯一性守卫（输出重复项供人工复核） ----
$dup = $final | Group-Object zh | Where-Object Count -gt 1
if ($dup) { Write-Host "!! 重名未消歧:"; $dup | ForEach-Object { Write-Host "  $($_.Name) x$($_.Count)" } }

# ---- id 推导 ----
function Get-Id($e, $used) {
    $launch = [string]$e.launch
    $id = $null
    if ($launch -match '^ms-settings:(.+)$') {
        $id = 'set-' + ($Matches[1].ToLower() -replace '[^a-z0-9]+', '-')
    } elseif ($launch -match '^windowsdefender://(.+)$') {
        $id = 'def-' + ($Matches[1].ToLower() -replace '[^a-z0-9]+', '-')
    } elseif ($launch -eq 'windowsdefender:') {
        $id = 'def-home'
    } elseif ($launch -match '^explorer\.exe\s+shell:::\{([0-9a-fA-F-]+)\}') {
        $id = 'sh-' + $Matches[1].Substring(0, 8).ToLower()
    } elseif ($launch -match '^explorer\.exe\s+shell:(.+)$') {
        $id = 'sh-' + ($Matches[1].ToLower() -replace '[^a-z0-9]+', '')
    } elseif ($launch -match '^control\.exe\s+/name\s+Microsoft\.(\w+)') {
        $id = 'cp-' + $Matches[1].ToLower()
    } else {
        $first = ($launch -split ' ')[0].Trim('"')
        $base = [System.IO.Path]::GetFileNameWithoutExtension($first).ToLower()
        if ($base -eq 'control') { $id = 'control' }
        elseif ($base -notin @('', 'explorer')) { $id = $base }
        else { $id = ([string]$e.en).ToLower() -replace '[^a-z0-9]+', '-' }
    }
    $id = $id.Trim('-')
    $n = 1; $unique = $id
    while ($used.ContainsKey($unique)) { $n++; $unique = "$id-$n" }
    $used[$unique] = $true
    return $unique
}

# ---- C# 字符串转义（普通字符串：反斜杠翻倍、引号转义） ----
function Esc([string]$s) {
    return $s.Replace('\', '\\').Replace('"', '\"')
}

$method = @{ 'system-tool'='Tool'; 'control-panel'='Cpl'; 'msc-console'='Msc'; 'settings'='Sys'; 'shell-folder'='Folder' }
$used = @{}
$sb = New-Object System.Text.StringBuilder
foreach ($e in $final) {
    $id = Get-Id $e $used
    $kw = @($e.kw | Where-Object { $_ -and $_.Trim() } | ForEach-Object { '"' + (Esc $_) + '"' })
    $kwText = ''
    if ($kw.Count -gt 0) { $kwText = ', ' + ($kw -join ', ') }
    $adminText = if ($e.admin) { 'true' } else { 'false' }
    [void]$sb.AppendLine(('        {0}(tools, "{1}", "{2}", "{3}", "{4}", {5}{6});' -f $method[$e.cat], (Esc $id), (Esc $e.zh), (Esc $e.en), (Esc $e.launch), $adminText, $kwText))
}

$body = $sb.ToString()

$head = @'
namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Models;

/// <summary>
/// Windows 系统组件内置目录：系统工具、控制面板项、MMC 管理控制台、
/// ms-settings 设置页、Windows 安全中心深链、系统特殊文件夹。静态只读数据，
/// 供 <see cref="SystemToolSearch"/> 检索直达。
///
/// 约定：
///  - Name 为微软官方简体中文名（全目录唯一）；NameEn 为英文名；
///  - Launch 为启动命令：URI（ms-settings:/windowsdefender:）或命令行（支持 %SystemRoot% 等环境变量），
///    裸命令名由 UI 层按 System32 → Windows → PATH 解析；
///  - RequiresAdmin 按可执行文件清单实测标注（regedit/msconfig=requireAdministrator；
///    mmc.exe=highestAvailable，故全部 .msc 控制台会触发 UAC；control.exe/.cpl=asInvoker）；
///  - Keywords 收录中文俗称与英文命令名（小写）；个别未逐一实测的条目在 Keywords 中带 "unverified" 标记，
///    本机不存在的条目（如家庭版无 gpedit.msc、未装 RSAT）由 UI 层可用性过滤剔除；
///  - 拼音由 <see cref="PinyinProvider"/> 按 Name 现算。
/// </summary>
public static class SystemToolCatalog
{
    /// <summary>全部系统组件条目</summary>
    public static IReadOnlyList<SystemTool> Tools { get; } = Build();

    private static List<SystemTool> Build()
    {
        var tools = new List<SystemTool>(256);
'@

$tail = @'

        return tools;
    }

    /// <summary>系统工具条目</summary>
    private static void Tool(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.Tool, admin, kw);

    /// <summary>控制面板项条目</summary>
    private static void Cpl(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.ControlPanel, admin, kw);

    /// <summary>MMC 管理控制台条目</summary>
    private static void Msc(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.MscConsole, admin, kw);

    /// <summary>Windows 设置页 / 安全中心深链条目</summary>
    private static void Sys(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.Settings, admin, kw);

    /// <summary>系统特殊文件夹条目</summary>
    private static void Folder(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.ShellFolder, admin, kw);

    private static void Add(List<SystemTool> list, string id, string zh, string en, string launch,
        SystemToolCategory category, bool admin, string[] kw)
    {
        list.Add(new SystemTool
        {
            Id = id,
            Name = zh,
            NameEn = en,
            Launch = launch,
            Category = category,
            RequiresAdmin = admin,
            Keywords = new List<string>(kw)
        });
    }
}
'@

$outPath = Join-Path $root 'src\QuickPalette.Domain\Services\SystemToolCatalog.cs'
Set-Content -Path $outPath -Value ($head + $body + $tail) -Encoding UTF8
Write-Host "生成完成：$outPath"
Write-Host "条目数：$($final.Count)（id 唯一 $($used.Count)）"
