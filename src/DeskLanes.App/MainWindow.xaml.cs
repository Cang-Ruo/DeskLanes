using System;
using System.ComponentModel;
using System.Windows;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;

namespace DeskLanes.App;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private bool _preferencesApplied;
    private bool _lastWasMaximized;
    private bool _userRestored;

    public MainWindow(MainViewModel viewModel, AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // 轻玻璃背景：Win11 原生 DWM 材料（Acrylic → Mica），失败自动回退不透明底色
        WindowBackdropService.Attach(this);
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// 进入全屏工作空间模式：直接以最大化呈现。
    /// 「全屏模式」是一种模式而不是一个尺寸，切过去却不占满屏幕会让人以为切错了。
    /// </summary>
    /// <param name="fromOtherMode">
    /// true 表示用户刚从别的模式切过来 —— 无脑铺满屏幕；
    /// false 表示重复进入（例如保存设置后重新应用模式）—— 用户若已自己还原过就别再弹回全屏。
    /// </param>
    public void BeginFullMode(bool fromOtherMode)
    {
        if (fromOtherMode)
        {
            // 切走再切回来，就该重新看到全屏：清掉上一轮的还原记录
            _userRestored = false;
        }
        else if (_userRestored)
        {
            // 这一轮用户已经主动还原过，尊重他的选择
            return;
        }

        if (WindowState != WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 只认「用户从最大化主动还原」这一种意图：拖标题栏、双击标题栏、还原按钮都算。
        // 程序自己置 Maximized 进不来这个分支（那时 _lastWasMaximized 还是 false）。
        if (_lastWasMaximized && WindowState == WindowState.Normal)
        {
            _userRestored = true;
        }

        _lastWasMaximized = WindowState == WindowState.Maximized;
    }

    /// <summary>窗口渲染完成后自动开始首次扫描与桌面监听（窗口先出现，避免白屏等待）。</summary>
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        ApplyWindowPreferences();

        // 三种模式共用的初始化（幂等）：哪个模式先显示就由它触发
        await _controller.EnsureInitializedAsync();
    }

    /// <summary>最小化到托盘（与"退出"区分：应用继续运行，热键与监听仍然有效）。</summary>
    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => Hide();

    private void ApplyWindowPreferences()
    {
        if (_preferencesApplied || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _preferencesApplied = true;

        AppSettings settings = viewModel.Workspace.Settings;
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        if (settings.WindowWidth > 0)
        {
            Width = settings.WindowWidth;
        }

        if (settings.WindowHeight > 0)
        {
            Height = settings.WindowHeight;
        }

        if (settings.WindowLeft is double left && settings.WindowTop is double top && IsPositionVisible(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>位置是否落在当前虚拟桌面范围内（防止多显示器变动后窗口跑到看不见的地方）。</summary>
    private static bool IsPositionVisible(double left, double top)
    {
        double x = SystemParameters.VirtualScreenLeft;
        double y = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;
        return left >= x - 48 && left < x + width - 120 && top >= y - 48 && top < y + height - 80;
    }

    /// <summary>关闭时保存窗口偏好，下次启动恢复；是否退出由控制器的托盘设置决定。</summary>
    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            // 侧边栏/悬浮窗模式下本窗口从未 Show 过，Left/Top 是 NaN；
            // 非有限值一律按 null 传（Core 层也有同样的守卫）。
            double rawLeft = WindowState == WindowState.Normal ? Left : double.NaN;
            double rawTop = WindowState == WindowState.Normal ? Top : double.NaN;

            _controller.SaveWindowPreferences(
                double.IsFinite(Width) ? Width : double.NaN,
                double.IsFinite(Height) ? Height : double.NaN,
                double.IsFinite(rawLeft) ? rawLeft : (double?)null,
                double.IsFinite(rawTop) ? rawTop : (double?)null,
                WindowState == WindowState.Maximized);
        }
    }
}
