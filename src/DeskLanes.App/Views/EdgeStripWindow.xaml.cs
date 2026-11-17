using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskLanes.App.Services;
using DeskLanes.Core;

namespace DeskLanes.App.Views;

/// <summary>
/// 收束后留在屏幕最左侧的指示器：侧栏半透明边栏 / 全屏靠近边界才弹出的窄条 /
/// 悬浮窗的 DeskLanes 珍珠冰晶玻璃球（阶段 40：低 Alpha 主体 + 三层边缘）。
/// 不抢焦点、不进任务栏、常驻置顶，否则被启动的软件盖住就找不回来了。
/// </summary>
public partial class EdgeStripWindow : Window
{
    /// <summary>小球被拖动后的新位置（物理像素），由外壳落盘。</summary>
    public event Action<double, double>? BallMoved;

    /// <summary>鼠标移到指示器上（侧边栏据此展开；全屏据此弹出窄条）。</summary>
    public event EventHandler? HoverRequested;

    /// <summary>点击指示器（全屏 / 悬浮窗据此展开；拖动过则不算点击）。</summary>
    public event EventHandler? ClickRequested;

    private static readonly SolidColorBrush HoverShellGlass =
        new(Color.FromArgb(0x66, 0x20, 0x33, 0x4A));

    private readonly ToolTip _openTip = new() { Content = "打开 DeskLanes" };
    private readonly ToolTip _dragTip = new() { Content = "拖动以移动" };
    private readonly Stopwatch _pressStopwatch = new();

    private CollapseVisual _visual = CollapseVisual.FullStrip;
    private CollapseVisualPolicy.OrbInteractionState _interaction =
        CollapseVisualPolicy.OrbInteractionState.Idle;
    private MonitorInterop.RECT _work;
    private double _dpiScale = 1;
    private int _widthPx;
    private int _heightPx;
    private int _currentLeft;
    private int _currentTop;
    private int _startCursorX;
    private int _startCursorY;
    private int _startLeft;
    private int _startTop;
    private bool _pressed;
    private bool _dragging;

    public EdgeStripWindow()
    {
        InitializeComponent();
        Bar.Background = ShellGlass();
        ApplyOrbVisualState(CollapseVisualPolicy.OrbVisualState.Idle);
    }

    /// <summary>侧栏 / 全屏窄条仍与三窗口本体共用同一支玻璃画刷。</summary>
    private static Brush ShellGlass()
        => Application.Current?.Resources["BrushShellGlass"] is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(0x4A, 0x20, 0x33, 0x4A));

    /// <summary>鼠标是否还停在指示器上（侧栏移开自动收束的判定用）。</summary>
    public bool IsPointerOver => Root.IsMouseOver;

    // ---------- 阶段 38：Glass Orb 视觉 ----------

    private static void AnimateOpacity(UIElement element, double target)
    {
        var animation = new DoubleAnimation(target,
            TimeSpan.FromSeconds(CollapseVisualPolicy.OrbTransitionSeconds))
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void AnimateScale(double target)
    {
        var duration = TimeSpan.FromSeconds(CollapseVisualPolicy.OrbTransitionSeconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = ease });
        OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = ease });
    }

    /// <summary>
    /// 阶段 40：主体画刷自带低 Alpha，三态用 Visibility 切换，
    /// 亮度差异来自边缘 / 高光 / 光带，不再靠加深主体填充。
    /// 波浪形状三态完全一致，只调透明度。
    /// </summary>
    private void ApplyOrbVisualState(CollapseVisualPolicy.OrbVisualState state)
    {
        if (_visual != CollapseVisual.FloatingBall)
        {
            return;
        }

        bool hover = state == CollapseVisualPolicy.OrbVisualState.Hover;
        bool drag = state == CollapseVisualPolicy.OrbVisualState.Dragging;
        bool active = hover || drag;

        OrbGlassIdle.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        OrbGlassHover.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
        OrbGlassDrag.Visibility = drag ? Visibility.Visible : Visibility.Collapsed;

        // 阶段 41：外光晕已移除；只用浅色环境阴影托住球体
        AnimateOpacity(OrbShadow, drag ? 0.3 : hover ? 0.27 : 0.24);

        // 边缘反射：三态的主要差异来源
        AnimateOpacity(OrbRimOuter, drag ? 1.0 : hover ? 0.96 : 0.88);
        AnimateOpacity(OrbRimInner, drag ? 0.62 : hover ? 0.52 : 0.4);
        AnimateOpacity(OrbIrisLeft, drag ? 0.9 : hover ? 0.78 : 0.62);
        AnimateOpacity(OrbIrisRight, drag ? 0.9 : hover ? 0.78 : 0.62);
        AnimateOpacity(OrbIrisBottom, drag ? 0.85 : hover ? 0.72 : 0.56);
        AnimateOpacity(OrbTopHighlight, drag ? 1.0 : hover ? 0.94 : 0.8);

        // 内部光影始终保持极淡，避免糊成实心
        AnimateOpacity(OrbFlowPearl, active ? 0.56 : 0.5);
        AnimateOpacity(OrbFlowCyan, active ? 0.5 : 0.45);
        AnimateOpacity(OrbFlowLavender, active ? 0.54 : 0.48);

        // 两条光带：形状不变，只提亮；两端由渐变自然淡出
        AnimateOpacity(OrbWaveMain, active ? 1.0 : 0.92);
        AnimateOpacity(OrbWaveSub, active ? 0.96 : 0.9);
        AnimateOpacity(OrbWaveMainGlow, drag ? 0.38 : hover ? 0.34 : 0.28);
        AnimateOpacity(OrbWaveSubGlow, drag ? 0.34 : hover ? 0.3 : 0.26);

        AnimateScale(drag ? 1.05 : hover ? 1.04 : 1.0);
    }

    private void SetOrbTooltip(ToolTip? tip)
    {
        if (ReferenceEquals(Root.ToolTip, tip))
        {
            return;
        }

        if (Root.ToolTip is ToolTip previous && previous.IsOpen)
        {
            previous.IsOpen = false;
        }

        Root.ToolTip = tip;
    }

    private void SetInteraction(CollapseVisualPolicy.OrbInteractionState state)
    {
        if (_visual != CollapseVisual.FloatingBall)
        {
            return;
        }

        _interaction = state;
        ApplyOrbVisualState(CollapseVisualPolicy.VisualForInteraction(state));

        switch (state)
        {
            case CollapseVisualPolicy.OrbInteractionState.Idle:
                SetOrbTooltip(null);
                Cursor = Cursors.Hand;
                Root.Cursor = Cursors.Hand;
                break;
            case CollapseVisualPolicy.OrbInteractionState.Hover:
                SetOrbTooltip(_openTip);
                Cursor = Cursors.Hand;
                Root.Cursor = Cursors.Hand;
                break;
            case CollapseVisualPolicy.OrbInteractionState.Dragging:
                SetOrbTooltip(_dragTip);
                Cursor = Cursors.SizeAll;
                Root.Cursor = Cursors.SizeAll;
                break;
            // PressPending 沿用 Hover 的视觉与提示，不额外闪烁
        }
    }

    /// <summary>全屏窄条弹出 / 收起；侧栏提亮；小球仅在非按下态响应悬停。</summary>
    public void SetHover(bool hovered)
    {
        if (_visual == CollapseVisual.FullStrip)
        {
            Bar.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
        }

        Bar.Background = hovered ? HoverShellGlass : ShellGlass();

        if (_visual == CollapseVisual.FloatingBall &&
            _interaction is CollapseVisualPolicy.OrbInteractionState.Idle
                        or CollapseVisualPolicy.OrbInteractionState.Hover)
        {
            SetInteraction(hovered
                ? CollapseVisualPolicy.OrbInteractionState.Hover
                : CollapseVisualPolicy.OrbInteractionState.Idle);
        }
    }

    // ---------- 定位 ----------

    /// <summary>
    /// 按当前形态摆到指定显示器工作区。侧栏 / 窄条用物理像素；
    /// Glass Orb 的 44 DIP 可视 / 56 DIP 热区全部在 WPF 侧用 DIP 表达，
    /// 只有传给 SetWindowPos 和落盘的位置是物理像素。
    /// </summary>
    public void ShowAtWorkArea(CollapseVisual visual, MonitorInterop.RECT work, uint dpi,
        double? ballLeft = null, double? ballTop = null)
    {
        _visual = visual;
        _work = work;
        _dpiScale = Math.Max(dpi / 96.0, 0.5);
        int bar = CollapseVisualPolicy.BarPixels;
        int orbPx = (int)Math.Round(CollapseVisualPolicy.OrbHitDip * _dpiScale);

        int widthPx = visual == CollapseVisual.FloatingBall
            ? orbPx
            : CollapseVisualPolicy.FullHotZonePixels;
        int heightPx = work.Height;
        int x = work.Left;
        int y = work.Top;

        if (visual == CollapseVisual.FloatingBall)
        {
            heightPx = orbPx;
            y = work.Top + Math.Max(0, (work.Height - orbPx) / 2);
            if (ballLeft.HasValue && ballTop.HasValue)
            {
                x = (int)Math.Round(ballLeft.Value);
                y = (int)Math.Round(ballTop.Value);
            }

            // 换分辨率 / 换主屏后别让小球留在屏幕外
            x = Clamp(x, work.Left, work.Left + work.Width - widthPx);
            y = Clamp(y, work.Top, work.Top + work.Height - heightPx);
        }

        bool isBall = visual == CollapseVisual.FloatingBall;
        Bar.Visibility = isBall ? Visibility.Collapsed : Visibility.Visible;
        OrbGroup.Visibility = isBall ? Visibility.Visible : Visibility.Collapsed;

        Bar.Width = bar / _dpiScale;
        Width = widthPx / _dpiScale;
        Height = heightPx / _dpiScale;
        _widthPx = widthPx;
        _heightPx = heightPx;
        _currentLeft = x;
        _currentTop = y;
        _pressed = false;
        _dragging = false;
        _pressStopwatch.Reset();
        Show();

        if (isBall)
        {
            SetInteraction(CollapseVisualPolicy.OrbInteractionState.Idle);
        }
        else
        {
            SetHover(false);
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = MonitorInterop.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
            MonitorInterop.SwpNoActivate | MonitorInterop.SwpNoZOrder);
    }

    private static int Clamp(int value, int min, int max)
        => max < min ? min : Math.Min(max, Math.Max(min, value));

    private static MonitorInterop.POINT CursorPos()
    {
        _ = MonitorInterop.GetCursorPos(out MonitorInterop.POINT pt);
        return pt;
    }

    // ---------- 长按 / 拖动状态机 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 侧栏 / 全屏窄条不拖动，但 OnMouseUp 仍按原有方式处理点击
        if (!CollapseVisualPolicy.CanDrag(_visual))
        {
            return;
        }

        MonitorInterop.POINT pt = CursorPos();
        _startCursorX = pt.X;
        _startCursorY = pt.Y;
        _startLeft = _currentLeft;
        _startTop = _currentTop;
        _pressed = true;
        _dragging = false;
        _pressStopwatch.Restart();
        SetInteraction(CollapseVisualPolicy.OrbInteractionState.PressPending);
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        MonitorInterop.POINT pt = CursorPos();
        int dx = pt.X - _startCursorX;
        int dy = pt.Y - _startCursorY;

        if (!_dragging)
        {
            double distanceDip = (Math.Abs(dx) + Math.Abs(dy)) / _dpiScale;
            if (!CollapseVisualPolicy.CanBeginOrbDrag(_pressStopwatch.ElapsedMilliseconds, distanceDip))
            {
                return;   // 350ms 未到，或位移不足 6 DIP：仍算点击候选
            }

            _dragging = true;
            SetInteraction(CollapseVisualPolicy.OrbInteractionState.Dragging);
        }

        int left = Clamp(_startLeft + dx, _work.Left, _work.Left + _work.Width - _widthPx);
        int top = Clamp(_startTop + dy, _work.Top, _work.Top + _work.Height - _heightPx);
        if (left == _currentLeft && top == _currentTop)
        {
            return;
        }

        _currentLeft = left;
        _currentTop = top;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // 全程用物理像素 SetWindowPos，不与 Window.Left/Top 混用
            _ = MonitorInterop.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
                MonitorInterop.SwpNoSize | MonitorInterop.SwpNoZOrder |
                MonitorInterop.SwpNoActivate);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool isBall = _visual == CollapseVisual.FloatingBall;
        bool dragged = isBall && _dragging;

        // 先把状态复位再释放捕获，LostMouseCapture 就不会重复保存位置
        _pressed = false;
        _dragging = false;
        _pressStopwatch.Stop();
        if (Root.IsMouseCaptured)
        {
            Root.ReleaseMouseCapture();
        }

        if (dragged)
        {
            BallMoved?.Invoke(_currentLeft, _currentTop);
        }
        else
        {
            ClickRequested?.Invoke(this, EventArgs.Empty);
        }

        if (isBall)
        {
            SetInteraction(IsMouseOver
                ? CollapseVisualPolicy.OrbInteractionState.Hover
                : CollapseVisualPolicy.OrbInteractionState.Idle);
        }

        e.Handled = true;
    }

    /// <summary>捕获意外丢失（Alt+Tab、系统弹窗等）时安全收尾，绝不误触发点击。</summary>
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_pressed && !_dragging)
        {
            return;
        }

        bool dragged = _dragging;
        _pressed = false;
        _dragging = false;
        _pressStopwatch.Stop();
        if (dragged)
        {
            BallMoved?.Invoke(_currentLeft, _currentTop);
        }

        if (_visual == CollapseVisual.FloatingBall)
        {
            SetInteraction(IsMouseOver
                ? CollapseVisualPolicy.OrbInteractionState.Hover
                : CollapseVisualPolicy.OrbInteractionState.Idle);
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        SetHover(true);
        HoverRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => SetHover(false);
}
