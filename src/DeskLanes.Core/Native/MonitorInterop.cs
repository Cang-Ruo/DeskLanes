using System.Runtime.InteropServices;

namespace DeskLanes.Core;

/// <summary>
/// 显示器枚举与 DPI 相关的 Win32 互操作。
/// <para>
/// 用途：多显示器（尤其混合 DPI）下取「窗口所在那块屏」的工作区，
/// 而不是 <c>SystemParameters.VirtualScreen*</c> 那种所有屏拼成的大矩形。
/// </para>
/// </summary>
public static class MonitorInterop
{
    /// <summary>取离窗口最近的显示器（窗口跨屏时也能拿到一个明确结果）。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>显示器标识位：主显示器。</summary>
    public const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;

        public RECT rcMonitor;

        public RECT rcWork;

        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;

        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    /// <summary>
    /// 阶段 31：鼠标当前是否落在窗口矩形内（物理像素，跨混合 DPI 也准）。
    /// 悬停展开侧边栏时 WPF 的 IsMouseOver 在「窗口突然出现在鼠标下方」
    /// 这种时序下不稳，用几何判定更可靠。
    /// </summary>
    public static bool IsPointerOverWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect) ||
            !GetCursorPos(out POINT pt))
        {
            return false;
        }

        return pt.X >= rect.Left && pt.X < rect.Right &&
               pt.Y >= rect.Top && pt.Y < rect.Bottom;
    }

    /// <summary>SetWindowPos 的 HWND_BOTTOM：把窗口放到 z 序最底层。</summary>
    public static readonly IntPtr HwndBottom = new(1);

    /// <summary>取指定物理坐标所在显示器的工作区（物理像素）。</summary>
    public static bool TryGetWorkAreaForPoint(POINT pt, out RECT work, out uint dpi)
    {
        work = default;
        dpi = 96;
        IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || info.rcWork.Width <= 0)
        {
            return false;
        }

        work = info.rcWork;
        GetDpiForMonitor(monitor, MdtEffectiveDpi, out dpi, out _);
        return true;
    }

    /// <summary>取鼠标当前所在显示器的工作区（全屏切模式时用）。</summary>
    public static bool TryGetWorkAreaForCursor(out RECT work, out uint dpi)
    {
        work = default;
        dpi = 96;
        return GetCursorPos(out POINT pt) && TryGetWorkAreaForPoint(pt, out work, out dpi);
    }

    /// <summary>取停靠侧（虚拟屏最左 / 最右）那块显示器的工作区，不依赖窗口句柄。</summary>
    public static bool TryGetWorkAreaForDockedSide(bool right, out RECT work, out uint dpi)
    {
        int vx = GetSystemMetrics(76);
        int vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78);
        int vh = GetSystemMetrics(79);
        var pt = new POINT { X = right ? vx + vw - 1 : vx, Y = vy + vh / 2 };
        return TryGetWorkAreaForPoint(pt, out work, out dpi);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>窗口所在显示器的有效 DPI（Win10 1607+）。失败返回 0。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>MDT_EFFECTIVE_DPI：系统当前对该显示器实际生效的 DPI。</summary>
    public const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// 取窗口所在显示器的工作区（物理像素）与该显示器的有效 DPI。
    /// <para>
    /// DPI 按<b>显示器句柄</b>取而不是 <see cref="GetDpiForWindow"/>：窗口刚跨屏拖过去时，
    /// 窗口自身的 DPI 要等 WM_DPICHANGED 处理完才更新，显示器句柄则永远是即时的。
    /// </para>
    /// </summary>
    public static bool TryGetWorkArea(IntPtr hwnd, out RECT work, out uint dpi)
    {
        work = default;
        dpi = 96;

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || info.rcWork.Width <= 0)
        {
            return false;
        }

        work = info.rcWork;

        if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX >= 48)
        {
            dpi = dpiX;
        }

        return true;
    }
}
