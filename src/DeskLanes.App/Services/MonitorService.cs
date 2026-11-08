using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskLanes.Core;

namespace DeskLanes.App.Services;

/// <summary>
/// 按「窗口所在那块屏」取工作区，并把物理像素换算成 WPF 的逻辑像素（DIP）。
/// <para>
/// 为什么要它：<see cref="SystemParameters.VirtualScreenWidth"/> 是所有显示器拼成的大矩形，
/// 拿它做钳制有两个盲区 —— ① 窗口放在一块窄屏上时，只要还在大矩形内就不会被拉回；
/// ② 窗口比所在屏还宽时（竖屏 720 DIP 放 780 DIP 的悬浮窗）也判不出来。
/// </para>
/// <para>
/// 换算口径：<b>尺寸</b>用窗口所在显示器的 DPI（WPF 渲染就是按本屏 DPI 走的）；
/// 位置在高 DPI 下有系统 DPI 参与，混合 DPI 时逐项换算并不可靠，
/// 因此这里只提供工作区尺寸给调用方做收缩用，位置钳制仍交给虚拟屏那套逻辑。
/// </para>
/// </summary>
public static class MonitorService
{
    /// <summary>
    /// 纯计算：把窗口左上角钳回虚拟屏范围内。抽成公开函数是为了能对它做回归测试
    /// （阶段 13 物理热插拔缺陷：显示器拔掉后窗口留在已不存在的负 x 坐标上）。
    /// </summary>
    /// <summary>热插拔中间态会给出明显不是真实显示器的极小工作区，此时不该收缩窗口。</summary>
    public static bool IsWorkAreaSane(Size work, double minWidth, double minHeight)
        => !work.IsEmpty && work.Width >= minWidth * 0.5 && work.Height >= minHeight * 0.5;

    public static (double Left, double Top) ClampToVirtualScreen(
        double left, double top, double windowWidth, double windowHeight,
        double virtualLeft, double virtualTop, double virtualWidth, double virtualHeight)
    {
        double maxLeft = Math.Max(virtualLeft, virtualLeft + virtualWidth - windowWidth);
        double maxTop = Math.Max(virtualTop, virtualTop + virtualHeight - windowHeight);
        double newLeft = double.IsFinite(left)
            ? Math.Clamp(left, virtualLeft, maxLeft) : virtualLeft + 8;
        double newTop = double.IsFinite(top)
            ? Math.Clamp(top, virtualTop, maxTop) : virtualTop + 8;
        return (newLeft, newTop);
    }

    /// <summary>窗口所在显示器的工作区尺寸（DIP）。取不到时返回 <see cref="Size.Empty"/>。</summary>
    public static Size GetWorkAreaSize(Window window)
    {
        if (window is null)
        {
            return Size.Empty;
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (!MonitorInterop.TryGetWorkArea(hwnd, out MonitorInterop.RECT work, out uint dpi))
        {
            return Size.Empty;
        }

        double scale = dpi / 96.0;
        return new Size(work.Width / scale, work.Height / scale);
    }

    /// <summary>
    /// 把窗口收缩到所在显示器工作区内（<b>只缩不放</b>，不会把用户手动调小的窗口撑大）。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="minWidth">窗口自己的最小宽度（保证收缩后不会小于它）。</param>
    /// <param name="minHeight">窗口自己的最小高度。</param>
    /// <param name="padding">四周预留的余量，避免贴死屏幕边缘。</param>
    /// <returns>是否真的改过尺寸。</returns>
    public static bool FitIntoWorkArea(Window window, double minWidth, double minHeight, double padding = 8)
    {
        Size work = GetWorkAreaSize(window);
        if (work.IsEmpty)
        {
            return false;
        }

        // 显示器热插拔的中间态可能让 MonitorFromWindow 返回一个极小的"工作区"
        // （实测拔下左侧竖屏再插回后，悬浮窗被收缩成 340×48 的细横条）。
        // 这种值明显不是真实显示器，直接跳过收缩，别把窗口压扁。
        if (!IsWorkAreaSane(work, minWidth, minHeight))
        {
            return false;
        }

        // 屏幕本身就比最小尺寸还窄时，以屏幕为准（否则窗口必然出界，宁可小于最小尺寸）
        double maxWidth = Math.Max(Math.Min(minWidth, work.Width), work.Width - padding);
        double maxHeight = Math.Max(Math.Min(minHeight, work.Height), work.Height - padding);

        bool changed = false;

        if (double.IsFinite(window.Width) && window.Width > maxWidth)
        {
            window.Width = maxWidth;
            changed = true;
        }

        if (double.IsFinite(window.Height) && window.Height > maxHeight)
        {
            window.Height = maxHeight;
            changed = true;
        }

        return changed;
    }
}
