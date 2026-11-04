using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;

namespace DeskLanes.App.Views;

/// <summary>
/// 悬浮窗（快速启动）。置顶、无任务栏按钮、Esc 隐藏、失焦可配置是否自动隐藏。
/// 窗口本身只负责呈现与键盘/焦点行为，全部数据来自共享视图模型。
/// <para>
/// <c>WindowStyle=None</c> 的非分层窗口（WindowChrome 接管系统行为）：
/// 系统不提供标题栏也不提供缩放边框，因此拖动（顶部热区 + <see cref="Window.DragMove"/>）
/// 与缩放（右下角 Thumb）都由本窗口自行实现，松手后位置与尺寸写回配置。
/// 背景材质走 <see cref="WindowBackdropService"/>（与主窗口同一套 Acrylic 路径）。
/// </para>
/// </summary>
public partial class FloatingWindow : Window
{
    private readonly FloatingWindowViewModel _viewModel;
    private readonly AppController _controller;
    private bool _preferencesApplied;

    public FloatingWindow(FloatingWindowViewModel viewModel, AppController controller)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        DataContext = viewModel;
        InitializeComponent();
        // paintWindowSurface: false —— 根 Border 会用圆角画这层底色，
        // 窗口自身再画一遍就是同一像素叠两层 tint（18%+18%≈33%），玻璃会发闷
        WindowBackdropService.Attach(this, paintWindowSurface: false);

        // 恢复上次拖到的位置与缩到的尺寸（必须晚于 InitializeComponent）
        ApplyWindowPreferences();

        // 拿到窗口句柄后先按所在显示器收缩一次，避免"先按 780 显示再缩回去"闪一下
        SourceInitialized += (_, _) => FitToCurrentMonitor();

        // 失焦自动隐藏：呼出式窗口的常规行为，可在设置里关闭
        Deactivated += OnDeactivated;
        _viewModel.HideRequested += (_, _) => _controller.HideFloating();

        ContentRendered += (_, _) => _ = _controller.EnsureInitializedAsync();
    }

    /// <summary>显示并激活窗口（快捷键呼出）。快捷键属于用户显式操作，允许强制前台。</summary>
    public void ShowAndActivate(bool forceForeground = true)
    {
        Show();
        WindowActivator.Activate(this, forceForeground);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _controller.HideFloating();
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_viewModel.HideOnDeactivate)
        {
            _controller.HideFloating();
        }
    }

    // ---------- 拖动 / 缩放 ----------

    /// <summary>
    /// 顶部热区按下：交给系统的 <see cref="Window.DragMove"/> 完成拖动。
    /// 由系统驱动意味着跨显示器移动、贴边等系统行为都是免费的。
    /// </summary>
    private void OnDragBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // DragMove 是同步的：按住不放直到松开才返回，所以保存要写在它后面。
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 极少数情况（鼠标已被别的元素捕获）会抛；不让用户看到异常
            return;
        }

        PersistGeometry();
        e.Handled = true;
    }

    /// <summary>
    /// 八方向缩放：热区的 <see cref="FrameworkElement.Tag"/> 标了方向
    /// （T/B/L/R/TL/TR/BL/BR），这里按标记决定改宽高还是同时挪左上角。
    /// </summary>
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string direction)
        {
            return;
        }

        double dx = e.HorizontalChange;
        double dy = e.VerticalChange;

        // 左/上边缩放时窗口左上角要跟着走；同时守住最小尺寸，
        // 到底之后左上角就不再继续移动（否则会出现"边框不动但窗口在长大"的怪现象）。
        if (direction.Contains('L'))
        {
            double newWidth = Math.Max(MinWidth, Width - dx);
            Left += Width - newWidth;
            Width = newWidth;
        }
        else if (direction.Contains('R'))
        {
            Width = Math.Max(MinWidth, Width + dx);
        }

        if (direction.Contains('T'))
        {
            double newHeight = Math.Max(MinHeight, Height - dy);
            Top += Height - newHeight;
            Height = newHeight;
        }
        else if (direction.Contains('B'))
        {
            Height = Math.Max(MinHeight, Height + dy);
        }
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
        => PersistGeometry();

    /// <summary>
    /// 拖动或缩放结束后：先按所在显示器工作区收缩（拖到竖屏会自动收窄），
    /// 再拉回虚拟桌面范围内，最后写进配置。
    /// </summary>
    private void PersistGeometry()
    {
        FitToCurrentMonitor();
        _controller.ClampFloatingWindow();

        _controller.SaveFloatingWindowPreferences(
            double.IsFinite(Width) ? Width : double.NaN,
            double.IsFinite(Height) ? Height : double.NaN,
            double.IsFinite(Left) ? Left : (double?)null,
            double.IsFinite(Top) ? Top : (double?)null);
    }

    /// <summary>
    /// 显示后调用：按所在显示器收缩尺寸，再拉回屏幕范围内。
    /// 竖屏副屏逻辑宽只有 720 DIP，而这个窗口默认 780 DIP —— 不收缩必然出界。
    /// </summary>
    public void ApplyScreenConstraints()
    {
        FitToCurrentMonitor();
        _controller.ClampFloatingWindow();
    }

    /// <summary>把窗口收缩到当前显示器工作区内（只缩不放）。</summary>
    private void FitToCurrentMonitor()
        => MonitorService.FitIntoWorkArea(this, MinWidth, MinHeight);

    // ---------- 偏好恢复 ----------

    private void ApplyWindowPreferences()
    {
        if (_preferencesApplied)
        {
            return;
        }

        _preferencesApplied = true;

        AppSettings settings = _viewModel.Workspace.Settings;

        // 位置记录是「用户真的拖动/缩放过」的唯一标志：尺寸和位置是一起写的。
        // 只有位置有效时才连带恢复尺寸 —— 旧版本里这四个字段是没人用的死字段，
        // 配置里躺着的是当年的默认尺寸（620×520），直接照搬会把现在 XAML 里的
        // 默认尺寸顶掉，升级后窗口反而变小。
        if (settings.FloatingWindowLeft is double left
            && settings.FloatingWindowTop is double top
            && IsPositionVisible(left, top))
        {
            if (settings.FloatingWindowWidth >= MinWidth)
            {
                Width = settings.FloatingWindowWidth;
            }

            if (settings.FloatingWindowHeight >= MinHeight)
            {
                Height = settings.FloatingWindowHeight;
            }

            Left = left;
            Top = top;
            return;
        }

        // 没拖动过，或历史位置落在已经拔掉的显示器上：
        // 尺寸沿用 XAML 的默认值，位置改为居中（Manual 默认是屏幕左上角，很难看）。
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>位置是否还在当前虚拟桌面内（防止显示器变动后窗口跑到看不见的地方）。</summary>
    private static bool IsPositionVisible(double left, double top)
    {
        double x = SystemParameters.VirtualScreenLeft;
        double y = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;
        return left >= x - 24 && left < x + width - 60 && top >= y - 24 && top < y + height - 40;
    }
}
