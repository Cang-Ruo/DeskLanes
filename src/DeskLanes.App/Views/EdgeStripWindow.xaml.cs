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
/// 阶段 36：小球可拖动并记住位置；玻璃配色与三窗口本体共用同一支画刷。
/// </summary>
public partial class EdgeStripWindow : Window
{
    /// <summary>阶段 36：小球被拖动后的新位置（物理像素），由外壳落盘。</summary>
    public event Action<double, double>? BallMoved;

    /// <summary>鼠标移到指示器上（侧边栏据此展开；全屏据此弹出窄条；小球据此提亮）。</summary>
    public event EventHandler? HoverRequested;

    /// <summary>点击指示器（全屏 / 悬浮窗据此展开；拖动过则不算点击）。</summary>
    public event EventHandler? ClickRequested;

    private static readonly SolidColorBrush HoverShellGlass =
        new(Color.FromArgb(0x66, 0x20, 0x33, 0x4A));   // 同色系，稍浓一点

    private CollapseVisual _visual = CollapseVisual.FullStrip;
    private MonitorInterop.RECT _work;
    private int _widthPx;
    private int _heightPx;
    private int _currentLeft;
    private int _currentTop;
    private bool _pressed;
    private bool _moved;
    private int _startCursorX;
    private int _startCursorY;
    private int _startLeft;
    private int _startTop;

    public EdgeStripWindow()
    {
        InitializeComponent();
        Bar.Background = ShellGlass();
        Ball.Fill = ShellGlass();
        Ball.Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x7A, 0xA8, 0xD8));
        Ball.StrokeThickness = 1;
    }

    /// <summary>
    /// 阶段 36：与悬浮窗 / 全屏 / 侧边栏本体共用 GlassTheme 的 BrushShellGlass，
    /// 收起态不再是一块粉灰白玻璃。取不到资源时（设计时 / 独立宿主）用同色兜底。
    /// </summary>
    private static Brush ShellGlass()
        => Application.Current?.Resources["BrushShellGlass"] is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(0x4A, 0x20, 0x33, 0x4A));

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

        Brush brush = hovered ? HoverShellGlass : ShellGlass();
        Bar.Background = brush;
        Ball.Fill = brush;
    }

    /// <summary>
    /// 按当前模式的形态摆到指定显示器工作区（物理像素定位）。
    /// 尺寸来自 CollapseVisualPolicy：0.5cm 边栏 / 3cm 小球 / 全屏两倍宽热区。
    /// ballLeft/ballTop 为上次拖动后保存的位置（物理像素），null 用默认左边缘中部。
    /// </summary>
    public void ShowAtWorkArea(CollapseVisual visual, MonitorInterop.RECT work, uint dpi,
        double? ballLeft = null, double? ballTop = null)
    {
        _visual = visual;
        _work = work;
        double scale = Math.Max(dpi / 96.0, 0.5);
        int bar = CollapseVisualPolicy.BarPixels;
        int ball = CollapseVisualPolicy.BallPixels;

        // 小球是 3cm 圆；边栏 / 窄条都用 2 倍宽的透明热区，可见玻璃恒为 0.5cm。
        int widthPx = visual == CollapseVisual.FloatingBall
            ? ball
            : CollapseVisualPolicy.FullHotZonePixels;
        int heightPx = work.Height;
        int x = work.Left;
        int y = work.Top;

        if (visual == CollapseVisual.FloatingBall)
        {
            heightPx = ball;
            y = work.Top + Math.Max(0, (work.Height - ball) / 2);
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
        Ball.Visibility = isBall ? Visibility.Visible : Visibility.Collapsed;

        Bar.Width = bar / scale;   // 可见玻璃恒为 0.5cm，与窗口热区宽度无关
        Ball.Width = ball / scale;
        Ball.Height = ball / scale;

        Width = widthPx / scale;
        Height = heightPx / scale;
        _widthPx = widthPx;
        _heightPx = heightPx;
        _currentLeft = x;
        _currentTop = y;
        _pressed = false;
        _moved = false;
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

    private static int Clamp(int value, int min, int max)
        => max < min ? min : Math.Min(max, Math.Max(min, value));

    private static MonitorInterop.POINT CursorPos()
    {
        _ = MonitorInterop.GetCursorPos(out MonitorInterop.POINT pt);
        return pt;
    }

    // ---------- 阶段 36：只有小球可拖动 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        _moved = false;
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
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;   // 小于 4px 仍算点击，避免手抖误判成拖动
        }

        _moved = true;
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
            _ = MonitorInterop.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
                MonitorInterop.SwpNoSize | MonitorInterop.SwpNoZOrder |
                MonitorInterop.SwpNoActivate);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Root.IsMouseCaptured)
        {
            Root.ReleaseMouseCapture();
        }

        bool dragged = _moved;
        _pressed = false;
        _moved = false;

        if (dragged)
        {
            BallMoved?.Invoke(_currentLeft, _currentTop);
        }
        else
        {
            ClickRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        SetHover(true);
        HoverRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => SetHover(false);
}
