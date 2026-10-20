using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickPalette.Infrastructure.Launchers;

/// <summary>
/// 在资源管理器中"打开所在目录并选中目标"。
///
/// 为什么不用 explorer.exe /select,"...":
/// 那是命令行 hack，在部分 Windows 版本上带引号的目标会被解析错，
/// explorer 会落到默认文件夹（用户实测"总是跳到我的文档"）。
/// 这里优先使用系统 API SHOpenFolderAndSelectItems——无命令行解析、各版本行为一致；
/// 失败时才降级 explorer /select，再降级为直接打开所在目录。
/// </summary>
public static class ExplorerHelper
{
    /// <summary>
    /// 打开 <paramref name="path"/> 的所在目录并选中它（文件或目录皆可）。
    /// 返回 true = 已成功发起；失败返回 false 并经 <paramref name="warn"/> 汇报降级原因（可空）。
    /// 绝不抛异常。
    /// </summary>
    public static bool RevealInExplorer(string? path, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            warn?.Invoke("路径为空，无法定位");
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"路径不合法: {ex.Message}");
            full = path;
        }

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            warn?.Invoke($"无法确定所在目录: {full}");
            return OpenDirectoryFallback(full, warn);
        }

        // 1) 系统 API：父目录 + 选中子项（ILFindChild 得到相对 PIDL）
        if (TryRevealViaShellApi(parent, full, warn))
        {
            return true;
        }

        // 2) 降级：explorer /select（带尾随空格，规避部分版本的引号解析缺陷）
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\" ",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"explorer /select 失败: {ex.Message}");
        }

        // 3) 最后：直接打开所在目录
        return OpenDirectoryFallback(parent, warn);
    }

    private static bool TryRevealViaShellApi(string parent, string full, Action<string>? warn)
    {
        IntPtr folderPidl = IntPtr.Zero;
        IntPtr itemPidl = IntPtr.Zero;
        try
        {
            folderPidl = ILCreateFromPathW(parent);
            itemPidl = ILCreateFromPathW(full);
            if (folderPidl == IntPtr.Zero || itemPidl == IntPtr.Zero)
            {
                warn?.Invoke($"ILCreateFromPathW 失败: parent={parent}, item={full}");
                return false;
            }

            var relative = ILFindChild(folderPidl, itemPidl);
            if (relative == IntPtr.Zero)
            {
                warn?.Invoke($"ILFindChild 失败: {full} 不是 {parent} 的直接子项");
                return false;
            }

            var hr = SHOpenFolderAndSelectItems(folderPidl, 1, new[] { relative }, 0);
            if (hr != 0)
            {
                warn?.Invoke($"SHOpenFolderAndSelectItems 返回 0x{hr:X8}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"SHOpenFolderAndSelectItems 异常: {ex.Message}");
            return false;
        }
        finally
        {
            // relative 指向 folderPidl 内部，不单独释放；只释放两个完整 PIDL
            if (itemPidl != IntPtr.Zero) ILFree(itemPidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    private static bool OpenDirectoryFallback(string dir, Action<string>? warn)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"打开目录失败: {ex.Message}");
            return false;
        }
    }

    #region Win32 API

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindChild(IntPtr pidlParent, IntPtr pidlChild);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    #endregion
}
