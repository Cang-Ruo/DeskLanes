using System;
using System.ComponentModel;
using System.Windows;
using DeskLanes.Core;
using System.Windows.Interop;
using System.Windows.Input;
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

    // 自动收缩相关：把"用户想要的大小"和"收缩后实际显示的大小"分开记，
    // 否则在竖屏上开一次就会把 1180 永久改成 712，回到大屏还是小窗口。
    private double _intendedWidth = double.NaN;
    private double _intendedHeight = double.NaN;
    private double _fittedWidth = double.NaN;
    private double _fittedHeight = double.NaN;
    private bool _autoFitted;

    public MainWindow(MainViewModel viewModel, AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // 轻玻璃背景：Win11 原生 DWM 材料（Acrylic → Mica），失败自动回退不透明底色
        WindowBackdropService.Attach(this);
        StateChanged += OnStateChanged;
        SizeChanged += (_, _) => UpdateNarrowLayout();

        // 窗口偏好必须在窗口定位之前应用：句柄一建立就恢复上次的尺寸与位置。
        // 原来放在 ContentRendered 里，会晚于 BeginFullMode 的最大化 —— 用户上次
        // 把窗口留在副屏时，这次就会先在主屏最大化，副屏偏好直接丢失。
        SourceInitialized += (_, _) => ApplyWindowPreferences();

        // 窗口真正落到某块屏上之后（此时句柄已定位）才按那块屏收缩。
        // 放在 SourceInitialized 里做会踩空：那一刻还没定位，取到的是错的显示器。
        Loaded += (_, _) => FitToScreenAfterShow();
        Activated += (_, _) => FitToScreenAfterShow();
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

        EnsureFitsOnScreen();
    }

    /// <summary>
    /// 阶段 22：切到全屏前，把窗口移到「用户当前操作的那块显示器」再最大化。
    /// WPF 是按窗口当前所在屏最大化的，用户把悬浮窗拖到主屏不会带着全屏走；
    /// 用鼠标所在屏（点按钮/托盘时鼠标就在目标屏）最符合直觉。
    /// 坐标全用物理像素 + SetWindowPos，避开混合 DPI 下 DIP 位置换算不准的问题。
    /// </summary>
    public void PlaceOnCursorMonitor()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero ||
            !MonitorInterop.TryGetWorkAreaForCursor(out MonitorInterop.RECT work, out _))
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        MonitorInterop.SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0,
            MonitorInterop.SwpNoSize | MonitorInterop.SwpNoZOrder | MonitorInterop.SwpNoActivate);
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 把窗口收缩到所在显示器的工作区内（**只缩不放**，不会把用户调小的窗口撑大）。
    /// 最大化时由系统处理，直接跳过。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FitToScreenAfterShow"/> 的区别：这个是纯粹的"按当前尺寸收缩"，
    /// 不记录意图尺寸；对外（DPI/显示器变化）调用走它。
    /// </remarks>
    private void EnsureFitsOnScreen()
    {
        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        MonitorService.FitIntoWorkArea(this, MinWidth, MinHeight);
    }

    /// <summary>
    /// 窗口显示后按所在屏收缩一次，并记住"收缩前用户想要的大小"。
    /// 竖屏（逻辑宽 720）放不下默认的 1180 时会在还原态溢出，这是最后一个竖屏残留。
    /// </summary>
    private void FitToScreenAfterShow()
    {
        if (WindowState == WindowState.Maximized || !IsLoaded)
        {
            return;
        }

        if (double.IsNaN(_intendedWidth) && double.IsFinite(Width))
        {
            _intendedWidth = Width;
            _intendedHeight = Height;
        }

        if (!MonitorService.FitIntoWorkArea(this, MinWidth, MinHeight))
        {
            return;
        }

        // 记下收缩后的实际尺寸：关闭时用它判断"用户有没有自己改过大小"
        _autoFitted = true;
        _fittedWidth = Width;
        _fittedHeight = Height;
    }

    /// <summary>
    /// 窄屏（竖屏副屏逻辑宽只有 720 DIP）下标题栏放不下：折叠副标题，把宽度让给右侧按钮。
    /// 阈值 900 —— 宽屏 2048 与窄屏 720 之间有足够余量，不会因为几个像素抖动来回切。
    /// </summary>
    private void UpdateNarrowLayout()
    {
        if (SubtitleText is null)
        {
            return;
        }

        double width = ActualWidth;
        if (double.IsNaN(width) || width <= 0)
        {
            // 还没完成布局，等 SizeChanged 再来
            return;
        }

        SubtitleText.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 只认「用户从最大化主动还原」这一种意图：拖标题栏、双击标题栏、还原按钮都算。
        // 程序自己置 Maximized 进不来这个分支（那时 _lastWasMaximized 还是 false）。
        if (_lastWasMaximized && WindowState == WindowState.Normal)
        {
            _userRestored = true;

            // 从最大化还原：尺寸回到"上次普通状态"的值，在当前屏上可能放不下
            // （竖屏逻辑宽只有 720，默认却是 1180），按当前屏再收一次。
            FitToScreenAfterShow();
        }

        _lastWasMaximized = WindowState == WindowState.Maximized;
    }

    /// <summary>窗口渲染完成后自动开始首次扫描与桌面监听（窗口先出现，避免白屏等待）。</summary>
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 三种模式共用的初始化（幂等）：哪个模式先显示就由它触发
        await _controller.EnsureInitializedAsync();
    }

    /// <summary>最小化到托盘（与"退出"区分：应用继续运行，热键与监听仍然有效）。</summary>
    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// 无边框全屏没有系统关闭按钮，Esc 必须能退出（隐藏到托盘，应用继续运行）。
    /// 与顶部「⌄ 托盘」按钮、Ctrl+Alt+Space 切模式互为兜底。
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void ApplyWindowPreferences()
    {
        if (_preferencesApplied || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _preferencesApplied = true;

        AppSettings settings = viewModel.Workspace.Settings;

        // 已经最大化（BeginFullMode 先跑过）：此时再动 Left/Top 会把窗口挪到别的屏，直接放弃。
        if (WindowState == WindowState.Maximized)
        {
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

        // 位置必须在最大化**之前**落下去：WPF 是按"当前所在那块屏"来最大化的，
        // 最大化之后再设 Left/Top 只会把窗口挪走（9.5 踩过）。
        // 之前这里在 WindowMaximized 分支里直接 return，等于丢掉了位置偏好 ——
        // 用户上次在副屏最大化，下次启动会在主屏最大化（鼠标在哪块屏就开在哪）。
        if (settings.WindowLeft is double left && settings.WindowTop is double top && IsPositionVisible(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
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

            double saveWidth = double.IsFinite(Width) ? Width : double.NaN;
            double saveHeight = double.IsFinite(Height) ? Height : double.NaN;

            // 窗口被自动收缩过、且用户没有再手动改过大小：存"本来想要的大小"。
            // 否则在竖屏上开一次就会把 1180 永久记成 712，回到大屏还是个小窗口。
            if (_autoFitted
                && double.IsFinite(_intendedWidth) && double.IsFinite(_intendedHeight)
                && Math.Abs(Width - _fittedWidth) < 1
                && Math.Abs(Height - _fittedHeight) < 1)
            {
                saveWidth = _intendedWidth;
                saveHeight = _intendedHeight;
            }

            _controller.SaveWindowPreferences(
                saveWidth,
                saveHeight,
                double.IsFinite(rawLeft) ? rawLeft : (double?)null,
                double.IsFinite(rawTop) ? rawTop : (double?)null,
                WindowState == WindowState.Maximized);
        }
    }
}
