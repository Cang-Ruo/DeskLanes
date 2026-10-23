using System.Runtime.InteropServices;
using System.Text;

namespace DeskSpace.Core;

/// <summary>窗口查找、消息投递与前台窗口相关的 Win32 互操作（单实例唤起、窗口恢复用）。</summary>
public static class WindowInterop
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>SW_RESTORE。</summary>
    public const int SwRestore = 9;

    /// <summary>SW_SHOW。</summary>
    public const int SwShow = 5;

    /// <summary>
    /// 尽力把窗口带到前台，返回是否真的成为前台窗口。
    /// <para>
    /// Windows 会限制"非前台进程"抢占前台：当这次激活不是由真实键鼠输入驱动时
    /// （例如托盘菜单项由消息触发、或自动化脚本用合成消息驱动），<c>SetForegroundWindow</c>
    /// 常常被拒绝、只闪一下任务栏。这里在直接调用失败后补一条系统提供的合法通道：
    /// 先把本线程挂到前台线程的输入队列上再请求前台，用完立即解绑，
    /// 不改动任何输入行为、不影响其他窗口。
    /// </para>
    /// </summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SwRestore);
        }

        if (SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd)
        {
            return true;
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground == hWnd)
        {
            return true;
        }

        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = false;

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            ShowWindow(hWnd, SwShow);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            return GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>按窗口标题查找顶层窗口（返回第一个匹配）。</summary>
    public static IntPtr FindByTitle(string title) => FindWindow(null, title);

    /// <summary>注册跨进程唯一的自定义消息（用于唤起已运行的实例）。</summary>
    public static int RegisterMessage(string name) => RegisterWindowMessage(name);
}
