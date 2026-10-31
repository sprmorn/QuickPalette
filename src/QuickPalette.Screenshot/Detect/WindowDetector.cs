namespace QuickPalette.Screenshot.Detect;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>检测到的窗口（智能框选候选）</summary>
public sealed record DetectedWindow(IntPtr Handle, string Title, Rectangle PixelBounds);

/// <summary>
/// 智能框选 · 窗口级检测：按 Z 序自顶向下找光标下的最上层可见窗口。
/// 过滤：本进程窗口（覆盖层/钉图自身）、最小化窗口、DWM 幽灵窗口（UWP 残影）。
/// </summary>
public static class WindowDetector
{
    /// <summary>找包含指定像素点的最上层窗口；找不到返回 null</summary>
    public static DetectedWindow? FindWindowAtPixel(Point pixelPoint)
    {
        DetectedWindow? found = null;
        var currentPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsCandidate(hWnd, currentPid))
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (!bounds.Contains(pixelPoint))
            {
                return true;
            }

            found = new DetectedWindow(hWnd, GetWindowTitle(hWnd), bounds);
            return false; // EnumWindows 按 Z 序自顶向下，第一个命中即最上层
        }, IntPtr.Zero);

        return found;
    }

    private static bool IsCandidate(IntPtr hWnd, uint currentPid)
    {
        if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
        {
            return false;
        }

        // 本进程的窗口（覆盖层/钉图/主面板）不作为候选
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == currentPid)
        {
            return false;
        }

        // DWM 幽灵窗口（UWP 最小化残影）：cloaked ≠ 0
        if (DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return true;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
