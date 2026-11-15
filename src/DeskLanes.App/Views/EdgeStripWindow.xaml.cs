using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskLanes.App.Services;
using DeskLanes.Core;

namespace DeskLanes.App.Views;

/// <summary>
/// 阶段 31 起：收束后留在屏幕最左侧的指示器；阶段 34 扩展成三种形态
/// （侧栏半透明边栏 / 全屏靠近边界才弹出的窄条 / 悬浮窗半透明小球）。
/// 不抢焦点、不进任务栏、常驻置顶，否则被启动的软件盖住就找不回来了。
/// 鼠标进入发 <see cref="HoverRequested"/>，点击发 <see cref="ClickRequested"/>，
/// 由 AppController 按窗口模式决定「悬停展开 / 仅弹出 / 点击展开」。
/// </summary>
public partial class EdgeStripWindow : Window
{
    private static readonly SolidColorBrush IdleGlass =
        new(Color.FromArgb(0x4D, 0xE8, 0xF1, 0xFF));   // ≈30% 淡玻璃

    private static readonly SolidColorBrush HoverGlass =
        new(Color.FromArgb(0x88, 0xE8, 0xF1, 0xFF));   // ≈53% 提亮

    private CollapseVisual _visual = CollapseVisual.FullStrip;

    /// <summary>鼠标移到指示器上（侧边栏据此展开；全屏据此弹出窄条；小球据此提亮）。</summary>
    public event EventHandler? HoverRequested;

    /// <summary>点击指示器（全屏 / 悬浮窗据此展开）。</summary>
    public event EventHandler? ClickRequested;

    public EdgeStripWindow()
    {
        InitializeComponent();
        Bar.Background = IdleGlass;
        Ball.Fill = IdleGlass;
        Ball.Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        Ball.StrokeThickness = 1;
    }

    /// <summary>鼠标是否还停在指示器上（侧栏移开自动收束的判定用）。</summary>
    public bool IsPointerOver => Root.IsMouseOver;

    /// <summary>
    /// 悬停反馈：全屏模式负责把窄条「弹出 / 收回去」；
    /// 其余模式只把半透明玻璃提亮一点。
    /// </summary>
    public void SetHover(bool hovered)
    {
        if (_visual == CollapseVisual.FullStrip)
        {
            Bar.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
        }

        SolidColorBrush brush = hovered ? HoverGlass : IdleGlass;
        Bar.Background = brush;
        Ball.Fill = brush;
    }

    /// <summary>
    /// 按当前模式的形态摆到指定显示器工作区最左侧（物理像素定位）。
    /// 尺寸来自 CollapseVisualPolicy：0.5cm 边栏 / 3cm 小球 / 全屏两倍宽热区。
    /// </summary>
    public void ShowAtWorkArea(CollapseVisual visual, MonitorInterop.RECT work, uint dpi)
    {
        _visual = visual;
        double scale = Math.Max(dpi / 96.0, 0.5);
        int bar = CollapseVisualPolicy.BarPixels;
        int ball = CollapseVisualPolicy.BallPixels;

        // 小球是 3cm 圆；边栏 / 窄条都用 2 倍宽的透明热区，可见玻璃恒为 0.5cm。
        // 热区宽一倍也让「鼠标靠近左边界」更好触发，并避开 150% DPI 竖屏上
        // 「19px 窗口被 WPF 折算变宽」的问题。
        int widthPx = visual == CollapseVisual.FloatingBall
            ? ball
            : CollapseVisualPolicy.FullHotZonePixels;
        int heightPx = work.Height;
        int x = work.Left;
        int y = work.Top;

        if (visual == CollapseVisual.FloatingBall)
        {
            heightPx = ball;
            y = work.Top + Math.Max(0, (work.Height - ball) / 2);  // 左边缘中部
        }

        bool isBall = visual == CollapseVisual.FloatingBall;
        Bar.Visibility = isBall ? Visibility.Collapsed : Visibility.Visible;
        Ball.Visibility = isBall ? Visibility.Visible : Visibility.Collapsed;

        Bar.Width = bar / scale;   // 可见玻璃恒为 0.5cm，与窗口热区宽度无关
        Ball.Width = ball / scale;
        Ball.Height = ball / scale;

        Width = widthPx / scale;
        Height = heightPx / scale;
        Show();
        SetHover(false);   // 全屏窄条默认隐藏；边栏 / 小球保持半透明常驻

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = MonitorInterop.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
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
