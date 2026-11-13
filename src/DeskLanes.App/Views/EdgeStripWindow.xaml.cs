using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskLanes.Core;

namespace DeskLanes.App.Views;

/// <summary>
/// 阶段 31：全屏 / 侧边栏「收束」后留在屏幕最左侧的那条细线。
/// 不抢焦点、不进任务栏、常驻置顶（否则被启动的软件盖住就找不回来了），
/// 鼠标进入会发 <see cref="HoverRequested"/>，点击发 <see cref="ClickRequested"/>，
/// 具体是「悬停即展开」还是「必须点击」由 AppController 按窗口模式决定。
/// </summary>
public partial class EdgeStripWindow : Window
{
    /// <summary>鼠标移到细线上（侧边栏模式据此展开；全屏模式仅高亮）。</summary>
    public event EventHandler? HoverRequested;

    /// <summary>点击细线（全屏模式据此展开）。</summary>
    public event EventHandler? ClickRequested;

    public EdgeStripWindow()
    {
        InitializeComponent();
    }

    /// <summary>鼠标是否还停在线上（收束判定用，避免刚展开就立刻收回去）。</summary>
    public bool IsPointerOver => Root.IsMouseOver;

    /// <summary>鼠标进入时把细线提亮一点，给出「可以点」的提示。</summary>
    public void SetHover(bool hovered)
    {
        Line.Width = hovered ? 4 : 2;
        Line.Background = new SolidColorBrush(
            hovered ? System.Windows.Media.Color.FromArgb(0x66, 0xE8, 0xF1, 0xFF)
                    : System.Windows.Media.Color.FromArgb(0x26, 0xE8, 0xF1, 0xFF));
    }

    /// <summary>
    /// 贴到指定显示器工作区的最左侧（物理像素定位，混合 DPI 下也不会跑偏）。
    /// 高度占满工作区，不盖任务栏。
    /// </summary>
    public void ShowAtWorkArea(MonitorInterop.RECT work, uint dpi)
    {
        double scale = Math.Max(dpi / 96.0, 0.5);
        Width = 8;
        Height = Math.Max(80, work.Height / scale);
        Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int widthPx = (int)Math.Round(8 * scale);
        _ = MonitorInterop.SetWindowPos(
            hwnd, IntPtr.Zero, work.Left, work.Top, widthPx, work.Height,
            MonitorInterop.SwpNoActivate | MonitorInterop.SwpNoZOrder);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        SetHover(true);
        HoverRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => SetHover(false);

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        ClickRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
