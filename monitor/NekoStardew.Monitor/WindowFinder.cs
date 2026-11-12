using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace NekoStardew.Monitor;

/// <summary>按进程名 / 标题找星露谷窗口，并给出它在屏幕上的物理像素矩形。</summary>
public static class WindowFinder
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public sealed record GameWindow(IntPtr Handle, string Title, RECT Bounds);

    private static readonly string[] ProcessNames =
    {
        "Stardew Valley",
        "StardewModdingAPI",
        "Stardew Valley (SMAPI)",
    };

    /// <summary>找星露谷主窗口；找不到返回 null。</summary>
    public static GameWindow? Find()
    {
        var candidatePids = new System.Collections.Generic.HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
                candidatePids.Add((uint)p.Id);
        }

        // 有的环境进程名被 Steam 包装过，退一步按标题兜底
        IntPtr found = IntPtr.Zero;
        string foundTitle = string.Empty;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (!candidatePids.Contains(pid)) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length == 0) return true;

            // SMAPI 是“控制台窗口 + 游戏窗口”同进程，控制台标题里带 running SMAPI，
            // 类名是各种 Console，必须排掉，否则抓的是控制台那一整块黑底。
            var clsBuf = new StringBuilder(256);
            GetClassName(hWnd, clsBuf, clsBuf.Capacity);
            var cls = clsBuf.ToString();
            if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS"
                or "PseudoConsoleWindow" or "VConsoleWindow")
                return true;

            // 注意：游戏窗口自己的标题也带 "running SMAPI ..."（SMAPI 会给窗口改标题），
            // 所以只能按窗口类名排控制台，不能按标题排。
            if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS"
                or "PseudoConsoleWindow" or "VConsoleWindow")
                return true;

            found = hWnd;
            foundTitle = title;
            return false; // 找到就停
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
            return null;

        RECT rect;
        // DWM 扩展边框是物理像素，比 GetWindowRect 更适合喂给 DXGI 裁剪
        if (DwmGetWindowAttribute(found, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(found, out rect))
                return null;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        return new GameWindow(found, foundTitle, rect);
    }

    public static bool IsForeground(IntPtr hWnd) => GetForegroundWindow() == hWnd;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
