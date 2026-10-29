using System.Windows;
using System.Windows.Interop;
using DeskLanes.Core;

namespace DeskLanes.App.Services;

/// <summary>
/// 窗口前台激活：确保"显示了"就等于"拿到焦点"。
/// <para>
/// WPF 的 <c>Window.Activate()</c> 内部走 <c>SetForegroundWindow</c>，而 Windows 会拒绝
/// 非前台进程抢占前台——此时调用静默失败，只闪一下任务栏：窗口看得见，键盘输入却进不去。
/// 真实场景里这种情况很常见，例如用户刚点了桌面空白处（焦点已交给 Explorer），
/// 再从托盘菜单切回全屏工作空间。
/// </para>
/// <para>
/// 这里先做一次常规 <c>Activate()</c>，再核对前台窗口到底是不是它；
/// 没拿到就走 <see cref="WindowInterop.ForceForeground"/> 补一次合法的前台请求。
/// 全程只影响本应用的窗口，不拦截、不改动任何输入行为。
/// </para>
/// </summary>
public static class WindowActivator
{
    /// <summary>激活窗口；<paramref name="forceForeground"/> 为 false 时不做强制前台兜底。</summary>
    public static void Activate(Window window, bool forceForeground = true)
    {
        if (window is null)
        {
            return;
        }

        window.Activate();

        if (!forceForeground)
        {
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (WindowInterop.GetForegroundWindow() != hwnd)
        {
            WindowInterop.ForceForeground(hwnd);
        }
    }
}
