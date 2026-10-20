namespace QuickPalette.Infrastructure.Scanners;

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using Serilog;

/// <summary>
/// 开始菜单扫描器 - 解析 .lnk 快捷方式为 RawApp（COM 全量释放、失败跳过不留假条目、长路径支持）
/// </summary>
public class StartMenuScanner : IAppSource
{
    /// <summary>传统 MAX_PATH 阈值</summary>
    private const int MaxPath = 260;

    /// <summary>IShellLinkW::GetPath 缓冲（允许长路径）</summary>
    private const int PathBufferCapacity = 4096;

    private readonly ILogger _logger;

    public string SourceId => "StartMenu";

    public StartMenuScanner(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 扫描用户级 + 全局开始菜单；同步 IO 放入线程池以提供真异步语义
    /// </summary>
    public async Task<IEnumerable<RawApp>> ScanAsync(CancellationToken ct = default)
    {
        var apps = await Task.Run(() =>
        {
            var results = new List<RawApp>();

            // 用户级开始菜单
            var userStartMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");

            // 全局开始菜单
            var commonStartMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");

            ScanDirectory(userStartMenu, results, ct);
            ScanDirectory(commonStartMenu, results, ct);
            return results;
        }, ct).ConfigureAwait(false);

        _logger.Information("StartMenu scan found {Count} apps", apps.Count);
        return apps;
    }

    private void ScanDirectory(string dir, List<RawApp> apps, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var rawApp = ParseLnkFile(file);
                    if (rawApp != null)
                    {
                        apps.Add(rawApp);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 解析失败：记日志跳过，绝不产出 Path=xxx.lnk 的假条目
                    _logger.Warning(ex, "Failed to parse shortcut, skipped: {File}", file);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to scan directory: {Dir}", dir);
        }
    }

    /// <summary>
    /// 解析 .lnk：成功返回 RawApp（Path 为解析出的目标），失败返回 null 并记日志
    /// </summary>
    private RawApp? ParseLnkFile(string lnkPath)
    {
        // 长路径：.lnk 自身路径超过 260 时加 \\?\ 前缀保证能加载
        if (!TryNormalizeLongPath(lnkPath, out var loadPath))
        {
            _logger.Warning("Shortcut path too long and cannot be normalized, skipped: {File}", lnkPath);
            return null;
        }

        using var com = new ComScope();
        try
        {
            var shellLink = com.Track((IShellLinkW)new CShellLink());
            var persistFile = com.Track((IPersistFile)shellLink);

            persistFile.Load(loadPath, 0);

            var targetBuffer = new System.Text.StringBuilder(PathBufferCapacity);
            shellLink.GetPath(targetBuffer, targetBuffer.Capacity, out _, 0);
            var targetPath = targetBuffer.ToString();

            // 解析失败（空目标）：跳过，不产出假条目
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.Warning("Shortcut has no resolvable target, skipped: {File}", lnkPath);
                return null;
            }

            // 长路径处理：目标超过 260 时用 \\?\ 前缀校验存在性，不存在则跳过并记日志
            if (!TryNormalizeLongPath(targetPath, out var resolvedPath) ||
                !File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                if (targetPath.Length > MaxPath)
                {
                    _logger.Warning("Long path target missing ({Length} chars), skipped: {File} -> {Target}",
                        targetPath.Length, lnkPath, targetPath);
                    return null;
                }

                // 目标不存在（可能是卸载残留），跳过不产出假条目
                _logger.Debug("Shortcut target does not exist, skipped: {File} -> {Target}", lnkPath, targetPath);
                return null;
            }

            var argsBuffer = new System.Text.StringBuilder(PathBufferCapacity);
            shellLink.GetArguments(argsBuffer, argsBuffer.Capacity);

            var workDirBuffer = new System.Text.StringBuilder(PathBufferCapacity);
            shellLink.GetWorkingDirectory(workDirBuffer, workDirBuffer.Capacity);

            // 图标位置（可选，取不到不阻塞）
            var iconPath = string.Empty;
            var iconBuffer = new System.Text.StringBuilder(PathBufferCapacity);
            shellLink.GetIconLocation(iconBuffer, iconBuffer.Capacity, out _);
            var iconLocation = iconBuffer.ToString();
            if (!string.IsNullOrWhiteSpace(iconLocation) && File.Exists(iconLocation))
            {
                iconPath = iconLocation;
            }

            return new RawApp
            {
                Name = Path.GetFileNameWithoutExtension(lnkPath),
                Path = resolvedPath,
                Args = argsBuffer.ToString(),
                WorkDir = workDirBuffer.ToString(),
                IconPath = iconPath,
                Type = ResolveAppType(resolvedPath),
                SourceId = SourceId
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // COM 解析失败：记日志返回 null（不再产出 Path=xxx.lnk 的假条目）
            _logger.Warning(ex, "COM parse failed for shortcut, skipped: {File}", lnkPath);
            return null;
        }
    }

    /// <summary>
    /// 长路径归一化：超过 260 时加 \\?\（UNC 用 \\?\UNC\）前缀；含非法字符返回 false
    /// </summary>
    private static bool TryNormalizeLongPath(string path, out string normalized)
    {
        normalized = path;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        if (path.Length < MaxPath)
            return true;

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return true;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            normalized = @"\\?\UNC\" + path[2..];
            return true;
        }

        normalized = @"\\?\" + path;
        return true;
    }

    /// <summary>
    /// 按目标扩展名推断应用类型
    /// </summary>
    private static AppType ResolveAppType(string targetPath)
    {
        if (Directory.Exists(targetPath))
            return AppType.Folder;

        var ext = Path.GetExtension(targetPath);
        return ext.ToLowerInvariant() switch
        {
            ".exe" or ".com" or ".bat" or ".cmd" => AppType.Exe,
            ".lnk" => AppType.Lnk,
            ".url" => AppType.Url,
            _ => AppType.Lnk
        };
    }

    #region COM 资源释放

    /// <summary>
    /// COM 作用域：统一跟踪并以 Marshal.FinalReleaseComObject 释放（同一 RCW 只释放一次）。
    /// IShellLinkW 与 IPersistFile 是同一 RCW 的两个接口，必须避免重复释放。
    /// </summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _tracked = new();

        public T Track<T>(T comObject) where T : class
        {
            ArgumentNullException.ThrowIfNull(comObject);

            if (Marshal.IsComObject(comObject))
            {
                foreach (var existing in _tracked)
                {
                    if (ReferenceEquals(existing, comObject))
                        return comObject; // 同一 RCW 不重复跟踪
                }

                _tracked.Add(comObject);
            }

            return comObject;
        }

        public void Dispose()
        {
            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                var comObject = _tracked[i];
                try
                {
                    if (Marshal.IsComObject(comObject))
                        Marshal.FinalReleaseComObject(comObject);
                }
                catch (Exception)
                {
                    // 释放失败不影响业务（对象生命周期交由 GC 兜底）
                }
            }

            _tracked.Clear();
        }
    }

    #endregion

    #region COM Interop

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, out WIN32_FIND_DATAW pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public int dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public int nFileSizeHigh;
        public int nFileSizeLow;
        public int dwReserved0;
        public int dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    #endregion
}
