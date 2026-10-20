# 轻启 QuickPalette 一键打包脚本
# 用法：pwsh -File build\publish.ps1 [-Version 1.0.0] [-Configuration Release] [-SelfContained] [-Installer]
# 产物：dist\轻启-<版本>\ 与 dist\轻启-<版本>.zip（-Installer 时另编译 Inno Setup 安装包）
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$outDir     = Join-Path $root "dist\轻启-$Version"
$workerDir  = Join-Path $root "build\out\worker"
$previewDir = Join-Path $root "build\out\previewhost"
$preview32Dir = Join-Path $root "build\out\previewhost32"
$appProj    = "src\QuickPalette.App\QuickPalette.App.csproj"
$workerProj = "src\QuickPalette.FileIndexWorker\QuickPalette.FileIndexWorker.csproj"
$previewProj = "src\QuickPalette.PreviewHost\QuickPalette.PreviewHost.csproj"
$preview32Proj = "src\QuickPalette.PreviewHost\x86\QuickPalette.PreviewHost32.csproj"

Write-Host "==> 清理旧输出" -ForegroundColor Cyan
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $workerDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $previewDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $preview32Dir -Recurse -Force -ErrorAction SilentlyContinue

$rid = "win-x64"
$commonArgs = @("-c", $Configuration, "--nologo")

Write-Host "==> 发布 FileIndexWorker（提权索引进程）" -ForegroundColor Cyan
# 辅助进程与主程序同为自包含/框架依赖，保证 VM 等无 .NET 运行时环境也可全部工作
$helperSelfContained = $SelfContained.IsPresent.ToString().ToLowerInvariant()
dotnet publish $workerProj @commonArgs -o $workerDir -r $rid --self-contained $helperSelfContained
if ($LASTEXITCODE -ne 0) { throw "FileIndexWorker 发布失败" }

Write-Host "==> 发布 PreviewHost（预览宿主进程，承载系统预览处理器）" -ForegroundColor Cyan
dotnet publish $previewProj @commonArgs -o $previewDir -r $rid --self-contained $helperSelfContained
if ($LASTEXITCODE -ne 0) { throw "PreviewHost 发布失败" }

Write-Host "==> 发布 PreviewHost32（x86 预览宿主，承载 32 位进程内处理器）" -ForegroundColor Cyan
dotnet publish $preview32Proj @commonArgs -o $preview32Dir -r win-x86 --self-contained $helperSelfContained
if ($LASTEXITCODE -ne 0) { throw "PreviewHost32 发布失败" }

Write-Host "==> 发布主程序" -ForegroundColor Cyan
# 先构建一次（不带 RID），保证 FileIndexWorker 的 bin 产物就位（App.csproj 会随构建复制 worker 到输出目录）
dotnet publish $appProj @commonArgs -o $outDir -r $rid --self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant())
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败" }

Write-Host "==> 确保 Worker / PreviewHost 全套文件进入发布目录" -ForegroundColor Cyan
# x86 宿主带独立子目录：自包含运行时文件名与 x64 相同，平铺会互相覆盖/错配（hostfxr 格式错误）
$helperCopies = @(
    @{ Dir = $workerDir;    Dest = $outDir },
    @{ Dir = $previewDir;   Dest = $outDir },
    @{ Dir = $preview32Dir; Dest = (Join-Path $outDir "previewhost32") }
)
foreach ($copy in $helperCopies) {
    New-Item -ItemType Directory -Path $copy.Dest -Force | Out-Null
    Get-ChildItem $copy.Dir -File | ForEach-Object {
        $dest = Join-Path $copy.Dest $_.Name
        if (-not (Test-Path $dest)) { Copy-Item $_.FullName $dest }
    }
}

Write-Host "==> 写入使用说明" -ForegroundColor Cyan
@"
轻启 QuickPalette v$Version
1. 双击 QuickPalette.App.exe 运行（首次启动会请求一次管理员权限用于文件索引，可拒绝或在设置中关闭文件搜索）。
2. 默认全局热键：Ctrl+Alt+Space（可在设置中修改）。
3. 数据保存在 %APPDATA%\QuickPalette\（apps.json/settings.json/backups/ 等），卸载/升级不会删除。
4. 需要 .NET 9 Desktop Runtime$(if ($SelfContained) { '（本包已自包含，无需安装运行时）' } else { '（未安装时会提示下载）' })。
"@ | Set-Content (Join-Path $outDir "说明.txt") -Encoding UTF8

Write-Host "==> 压缩 zip" -ForegroundColor Cyan
$zipPath = Join-Path $root "dist\轻启-$Version.zip"
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath

if ($Installer) {
    # ISCC 发现顺序：PATH → 注册表安装位置 → 常见安装目录（含用户级安装）
    $iscc = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $reg = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
                               "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" `
               -ErrorAction SilentlyContinue |
               Where-Object { $_.DisplayName -like "*Inno Setup*" -and $_.InstallLocation } |
               Select-Object -First 1
        $candidates = @()
        if ($reg) { $candidates += Join-Path $reg.InstallLocation "ISCC.exe" }
        $candidates += "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        $candidates += "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        $candidates += "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        foreach ($c in $candidates) {
            if ($c -and (Test-Path $c)) { $iscc = $c; break }
        }
    }
    if ($iscc) {
        Write-Host "==> 编译 Inno Setup 安装包（$iscc）" -ForegroundColor Cyan
        # 版本号注入安装脚本，保证与发布目录 dist\轻启-<版本>\ 严格对应
        & $iscc "/DMyAppVersion=$Version" (Join-Path $root "build\轻启-installer.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }
    } else {
        Write-Warning "未找到 Inno Setup (iscc.exe)，跳过安装包编译"
    }
}

Write-Host "==> 完成：$outDir / $zipPath" -ForegroundColor Green
