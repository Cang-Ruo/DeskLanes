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
/// 它是分层窗口（<c>WindowStyle=None</c> + <c>AllowsTransparency=True</c>），
/// 系统不提供标题栏也不提供缩放边框，因此拖动（顶部热区 + <see cref="Window.DragMove"/>）
/// 与缩放（右下角 Thumb）都由本窗口自行实现，松手后位置与尺寸写回配置。
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

        // 恢复上次拖到的位置与缩到的尺寸（必须晚于 InitializeComponent）
        ApplyWindowPreferences();

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

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        // 拖拽增量本身就是逻辑像素，与 Width/Height 同一坐标系，直接用。
        // 下限守在 MinWidth/MinHeight，避免缩成一条线。
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void OnResizeGripDragCompleted(object sender, DragCompletedEventArgs e)
        => PersistGeometry();

    /// <summary>拖动或缩放结束后：先拉回虚拟桌面范围内，再写进配置。</summary>
    private void PersistGeometry()
    {
        _controller.ClampFloatingWindow();

        _controller.SaveFloatingWindowPreferences(
            double.IsFinite(Width) ? Width : double.NaN,
            double.IsFinite(Height) ? Height : double.NaN,
            double.IsFinite(Left) ? Left : (double?)null,
            double.IsFinite(Top) ? Top : (double?)null);
    }

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
