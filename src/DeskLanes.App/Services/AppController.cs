using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using DeskLanes.App.ViewModels;
using DeskLanes.App.Views;
using DeskLanes.Core;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.Services;

/// <summary>
/// 应用控制器：三种窗口模式 + 全局快捷键 + 系统托盘 + 单实例的枢纽。
/// <para>
/// 职责边界：业务数据全部来自 <see cref="IWorkspaceService"/>（三种模式共用同一份），
/// 这里只负责"哪个窗口可见、快捷键注册没有、托盘菜单做什么"。
/// </para>
/// </summary>
public sealed class AppController : IDisposable
{
    private const string HostWindowTitle = "DeskLanesMessageHost";
    private const string SingleInstanceMutexName = "Local\\DeskLanes.SingleInstance.v1";
    private const string ShowInstanceMessageName = "DeskLanes.ShowExistingInstance";
    private const string TaskbarCreatedMessageName = "TaskbarCreated";

    private const int WmDisplayChange = 0x007E;

    // 托盘菜单命令 Id（>0 且避开系统菜单范围）
    private const int TrayIdOpen = 2001;
    private const int TrayIdModeFloating = 2002;
    private const int TrayIdModeFull = 2003;
    private const int TrayIdModeSidebar = 2004;
    private const int TrayIdSettings = 2005;
    private const int TrayIdExit = 2006;

    private readonly IDesktopScanner _scanner;
    private readonly IIconService _iconService;
    private readonly ILaunchService _launchService;
    private readonly IDialogService _dialogService;
    private readonly string _configDir;

    private MainViewModel? _mainViewModel;
    private FloatingWindowViewModel? _floatingViewModel;
    private SidebarViewModel? _sidebarViewModel;
    private WindowModeManager? _modeManager;
    private SettingsViewModel? _settingsViewModel;

    private MainWindow? _mainWindow;
    private FloatingWindow? _floatingWindow;
    private SidebarWindow? _sidebarWindow;
    private SettingsWindow? _settingsWindow;
    private Window? _messageHost;

    private HotkeyService? _hotkeyService;
    private TrayIconService? _tray;
    private IDesktopSyncService? _syncService;
    private IDiagnosticLogger? _logger;
    private IStartupService? _startupService;
    private IDesktopIconService? _desktopIconService;
    private Mutex? _instanceMutex;
    private IntPtr _messageHostHandle;
    private int _showInstanceMessageId;
    private int _taskbarCreatedMessageId;
    private bool _disposed;
    private bool _initialized;
    private Task? _initializationTask;

    public AppController(
        IDesktopScanner scanner,
        IIconService iconService,
        ILaunchService launchService,
        IDialogService dialogService,
        string configDir)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _configDir = configDir;
    }

    /// <summary>当前窗口模式（供界面展示）。</summary>
    public WindowMode CurrentMode => _modeManager?.CurrentMode ?? WindowMode.Full;

    /// <summary>全屏工作空间窗口（供 App 设置 MainWindow）。</summary>
    public MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// 启动：先做单实例检查，再创建三个窗口、注册热键与托盘，最后显示当前模式的窗口。
    /// 返回 false 表示已有实例在运行（调用方应直接退出）。
    /// </summary>
    public bool Startup()
    {
        if (!TryBecomeSingleInstance())
        {
            SignalExistingInstance();
            return false;
        }

        // ---- 组装共享数据与视图模型（三种模式共用同一份） ----
        var shellState = new ShellState();
        IShortcutResolver shortcutResolver = new ShortcutResolver();
        var libraryStore = new LibraryStore(_configDir);
        var configStore = new ConfigStore(_configDir);
        IWorkspaceService workspace = new WorkspaceService(libraryStore, configStore, shortcutResolver);

        _logger = new FileDiagnosticLogger(Path.Combine(_configDir, "logs"));
        _startupService = new StartupService();
        _desktopIconService = new DesktopIconService();
        var watchTargets = BuildWatchTargets(_logger);
        _syncService = new DesktopSyncService(workspace, _scanner, watchTargets, SyncOptions.Default, _logger);

        _mainViewModel = new MainViewModel(
            workspace, _scanner, _iconService, _launchService, _dialogService, shellState, _syncService);
        _floatingViewModel = new FloatingWindowViewModel(workspace, _dialogService, _launchService, shellState);
        _sidebarViewModel = new SidebarViewModel(workspace, _dialogService, _launchService, shellState);
        _modeManager = new WindowModeManager(workspace);

        // ---- 消息宿主窗口（热键与单实例唤起的消息都要落到 UI 线程） ----
        CreateMessageHost();

        // ---- 三个窗口（只创建，不一定显示） ----
        _mainWindow = new MainWindow(_mainViewModel, this) { DataContext = _mainViewModel };
        _floatingWindow = new FloatingWindow(_floatingViewModel, this);
        _sidebarWindow = new SidebarWindow(_sidebarViewModel, this);

        (_dialogService as WpfDialogService)?.SetOwner(_mainWindow);

        _mainWindow.Closing += OnMainWindowClosing;
        _floatingWindow.Closing += OnSecondaryWindowClosing;
        _sidebarWindow.Closing += OnSecondaryWindowClosing;

        _mainViewModel.ModeSwitchRequested += OnModeSwitchRequested;
        _modeManager.ModeChanged += OnModeChanged;
        _floatingViewModel.ModeSwitchRequested += OnModeSwitchRequested;
        _sidebarViewModel.ModeSwitchRequested += OnModeSwitchRequested;
        _floatingViewModel.HideRequested += (_, _) => _modeManager.HideFloating();

        // ---- 系统托盘（自实现，纯 P/Invoke，消息走消息宿主窗口） ----
        // 托盘要先于快捷键创建：快捷键注册失败时的气泡通知需要托盘已经就绪。
        _tray = new TrayIconService(_messageHostHandle)
        {
            ToolTipText = "DeskLanes",
        };
        _tray.MenuItems = new List<TrayMenuItem>
        {
            new("打开工作空间", TrayIdOpen),
            TrayMenuItem.Separator(),
            new("悬浮窗模式", TrayIdModeFloating),
            new("全屏模式", TrayIdModeFull),
            new("侧边栏模式", TrayIdModeSidebar),
            TrayMenuItem.Separator(),
            new("设置…", TrayIdSettings),
            TrayMenuItem.Separator(),
            new("退出 DeskLanes", TrayIdExit),
        };
        _tray.Activated += (_, _) => ShowCurrentMode();
        _tray.MenuCommand += OnTrayMenuCommand;

        // ---- 全局快捷键（托盘已就绪，注册失败时气泡可用） ----
        RegisterHotkeyWithFeedback();

        UpdateTrayText();

        // ---- 按当前模式显示对应窗口 ----
        // 启动不做强制前台：开机自启会在登录阶段运行，此时不该与其他进程做线程输入挂接。
        ApplyMode(_modeManager.CurrentMode, activate: true, forceForeground: false);

        _logger.Log(SyncLogLevel.Info, $"应用已启动，当前窗口模式：{_modeManager.CurrentMode}");
        return true;
    }

    /// <summary>首次扫描与桌面监听（幂等）：由当前模式的窗口渲染完成后调用。</summary>
    public Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        if (_initializationTask is not null)
        {
            return _initializationTask;
        }

        _initializationTask = RunInitializationAsync();
        return _initializationTask;
    }

    private async Task RunInitializationAsync()
    {
        try
        {
            if (_mainViewModel is not null)
            {
                await _mainViewModel.InitializeAsync();
            }
        }
        finally
        {
            _initialized = true;
        }
    }

    /// <summary>快捷键动作：呼出或隐藏悬浮窗。</summary>
    public void ToggleFloatingFromHotkey()
    {
        if (_modeManager is null)
        {
            return;
        }

        _modeManager.ToggleFloating();

        if (_modeManager.IsFloatingVisible && _floatingWindow is not null)
        {
            _floatingWindow.ShowAndActivate();
        }
        else
        {
            _floatingWindow?.Hide();
        }

        UpdateTrayText();
    }

    /// <summary>隐藏悬浮窗（Esc 由窗口自身处理，这里供外部调用）。</summary>
    public void HideFloating()
    {
        _modeManager?.HideFloating();
        _floatingWindow?.Hide();
        UpdateTrayText();
    }

    /// <summary>隐藏侧边栏（不退出应用）。</summary>
    public void HideSidebar()
    {
        _modeManager?.HideSidebar();
        _sidebarWindow?.Hide();
        UpdateTrayText();
    }

    /// <summary>显示当前模式的窗口（托盘"打开"）。</summary>
    public void ShowCurrentMode()
    {
        ApplyMode(_modeManager?.CurrentMode ?? WindowMode.Full, activate: true);
    }

    /// <summary>真正退出应用：释放热键、监听器、托盘等资源。</summary>
    public void ExitApplication()
    {
        _logger?.Log(SyncLogLevel.Info, "用户请求退出应用，开始释放资源。");
        Dispose();
        Application.Current?.Shutdown();
    }

    /// <summary>保存窗口偏好（关闭时由窗口调用）。</summary>
    public void SaveWindowPreferences(double width, double height, double? left, double? top, bool maximized)
        => _mainViewModel?.SaveWindowPreferences(width, height, left, top, maximized);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _hotkeyService?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _syncService?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略
        }

        if (_messageHost is not null)
        {
            try
            {
                _messageHost.Close();
            }
            catch
            {
                // 忽略
            }

            _messageHost = null;
        }

        _logger?.Log(SyncLogLevel.Info, "资源已释放（快捷键 / 文件监听 / 托盘）。");

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
            }
            catch
            {
                // 忽略
            }

            _instanceMutex = null;
        }
    }

    // ---------- 单实例 ----------

    private bool TryBecomeSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isNew);
            return isNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出：取得所有权，按新实例运行
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 无法创建互斥体（极少见）：放行，不阻断用户
            _logger?.Log(SyncLogLevel.Warning, "无法创建单实例互斥体，已按单实例放行。");
            return true;
        }
    }

    /// <summary>通知已在运行的实例显示窗口。</summary>
    private void SignalExistingInstance()
    {
        int messageId = WindowInterop.RegisterMessage(ShowInstanceMessageName);
        IntPtr existing = WindowInterop.FindByTitle(HostWindowTitle);
        if (existing != IntPtr.Zero)
        {
            WindowInterop.PostMessage(existing, messageId, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ---------- 消息宿主 ----------

    private void CreateMessageHost()
    {
        // 用一个极小的隐藏窗口承载消息循环：全局热键与跨进程唤起都需要窗口句柄
        _messageHost = new Window
        {
            Title = HostWindowTitle,
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -2000,
            Top = -2000,
        };

        _messageHost.Show();
        _messageHost.Hide();

        IntPtr handle = new WindowInteropHelper(_messageHost).Handle;
        _messageHostHandle = handle;
        _hotkeyService = new HotkeyService(handle);

        _showInstanceMessageId = WindowInterop.RegisterMessage(ShowInstanceMessageName);
        _taskbarCreatedMessageId = WindowInterop.RegisterMessage(TaskbarCreatedMessageName);

        HwndSource source = (HwndSource)PresentationSource.FromVisual(_messageHost)!;
        source.AddHook(OnHostMessage);
    }

    private IntPtr OnHostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessageId)
        {
            // Explorer 重启：托盘图标会丢失（托盘服务内部会重新注册）
            _logger?.Log(SyncLogLevel.Info, "检测到任务栏（Explorer）重启，正在重新注册托盘图标。");
        }

        // 托盘消息（图标回调 / 原生菜单 / TaskbarCreated）优先交给托盘服务
        if (_tray is not null && _tray.HandleMessage(msg, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == HotkeyInterop.WM_HOTKEY && wParam.ToInt32() == HotkeyService.HotkeyId)
        {
            ToggleFloatingFromHotkey();
            handled = true;
        }
        else if (msg == _showInstanceMessageId)
        {
            // 第二个实例在呼唤：显示当前模式窗口
            ShowCurrentMode();
            handled = true;
        }
        else if (msg == TrayInterop.WmCommand)
        {
            // 菜单命令经 WM_COMMAND 到达时（非 TPM_RETURNCMD 同步路径）
            handled = _tray?.HandleCommand(wParam) ?? false;
        }
        else if (msg == WmDisplayChange)
        {
            // 显示器配置变化：重新摆放侧边栏与悬浮窗，避免跑到屏幕外
            ClampWindowsToScreen();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>托盘菜单命令分发。</summary>
    private void OnTrayMenuCommand(object? sender, int id)
    {
        switch (id)
        {
            case TrayIdOpen:
                ShowCurrentMode();
                break;

            case TrayIdModeFloating:
                _modeManager?.SwitchTo(WindowMode.Floating);
                break;

            case TrayIdModeFull:
                _modeManager?.SwitchTo(WindowMode.Full);
                break;

            case TrayIdModeSidebar:
                _modeManager?.SwitchTo(WindowMode.Sidebar);
                break;

            case TrayIdSettings:
                ShowSettings();
                break;

            case TrayIdExit:
                ExitApplication();
                break;
        }
    }

    // ---------- 窗口模式 ----------

    /// <summary>
    /// 应用窗口模式。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    /// <param name="activate">是否把窗口带到前台。</param>
    /// <param name="forceForeground">
    /// 是否允许在常规 Activate 失败后走 <see cref="WindowInterop.ForceForeground"/>。
    /// 默认开启（托盘菜单 / 热键 / 设置保存都是用户显式操作）；
    /// <b>应用启动时传 false</b>——开机自启会在登录阶段运行，此时不应与其他线程做线程输入挂接。
    /// </param>
    private void ApplyMode(WindowMode mode, bool activate, bool forceForeground = true)
    {
        switch (mode)
        {
            case WindowMode.Floating:
                _mainWindow?.Hide();
                _sidebarWindow?.Hide();
                if (_floatingWindow is not null)
                {
                    if (activate)
                    {
                        _floatingWindow.ShowAndActivate(forceForeground);
                    }
                    else
                    {
                        _floatingWindow.Show();
                    }
                }

                break;

            case WindowMode.Sidebar:
                _mainWindow?.Hide();
                _floatingWindow?.Hide();
                if (_sidebarWindow is not null)
                {
                    // ShowDocked() 内部已经 Show + Activate；这里不再叠加前台强制，
                    // 避免与侧边栏「失焦自动收起」的判断互相干扰。
                    _sidebarWindow.ShowDocked();
                }

                break;

            default:
                _floatingWindow?.Hide();
                _sidebarWindow?.Hide();
                if (_mainWindow is not null)
                {
                    _mainWindow.Show();
                    if (activate)
                    {
                        if (_mainWindow.WindowState == WindowState.Minimized)
                        {
                            _mainWindow.WindowState = WindowState.Normal;
                        }

                        WindowActivator.Activate(_mainWindow, forceForeground);
                    }
                }

                break;
        }

        UpdateTrayText();
    }

    private void OnModeChanged(object? sender, WindowMode mode) => ApplyMode(mode, activate: true);

    private void OnModeSwitchRequested(object? sender, WindowMode mode) => _modeManager?.SwitchTo(mode);

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        AppSettings settings = _mainViewModel?.Workspace.Settings ?? new AppSettings();

        if (settings.MinimizeToTray)
        {
            // 关闭 = 隐藏到托盘，应用继续运行（与"退出"明确区分）
            e.Cancel = true;
            _mainWindow?.Hide();
            _logger?.Log(SyncLogLevel.Info, "窗口已隐藏到托盘（应用继续运行）。");
        }
    }

    private void OnSecondaryWindowClosing(object? sender, CancelEventArgs e)
    {
        // 悬浮窗 / 侧边栏的关闭按钮只是隐藏，不影响应用与其他模式
        e.Cancel = true;
        (sender as Window)?.Hide();

        if (sender == _floatingWindow)
        {
            _modeManager?.HideFloating();
        }

        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_tray is null || _modeManager is null)
        {
            return;
        }

        _tray.ToolTipText = _modeManager.CurrentMode switch
        {
            WindowMode.Floating => "DeskLanes · 悬浮窗（已呼出）",
            WindowMode.Sidebar => "DeskLanes · 侧边栏",
            _ => "DeskLanes · 全屏工作空间",
        };
        _tray.UpdateTip();
    }

    // ---------- 设置 ----------

    private void ShowSettings()
    {
        if (_mainViewModel is null || _hotkeyService is null)
        {
            return;
        }

        // 每次打开设置都重新读一次系统实际状态：用户可能在软件运行期间
        // 用 Windows 自己的右键菜单 / 任务管理器改过这两项，启动时的快照会过时。
        _desktopIconService?.Refresh();
        _startupService?.Refresh();

        if (_settingsWindow is not null)
        {
            WindowActivator.Activate(_settingsWindow);
            return;
        }

        _settingsViewModel = new SettingsViewModel(
            _mainViewModel.Workspace, _hotkeyService, _dialogService,
            _startupService, _desktopIconService, _configDir);
        _settingsWindow = new SettingsWindow(_settingsViewModel)
        {
            Owner = _mainWindow,
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _settingsViewModel = null;
        };

        _settingsViewModel.Saved += (_, _) =>
        {
            // 应用新的窗口模式
            _modeManager?.SwitchTo(_mainViewModel.Workspace.Settings.WindowMode);

            // 侧边栏几何可能变化：重新摆放
            if (_sidebarWindow is not null && _sidebarWindow.IsVisible)
            {
                _sidebarWindow.ShowDocked();
            }

            UpdateTrayText();
        };

        _settingsWindow.ShowDialog();
    }

    // ---------- 多显示器 / DPI ----------

    /// <summary>把可见窗口拉回当前虚拟桌面范围内（显示器配置变化后调用）。</summary>
    private void ClampWindowsToScreen()
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;

        ClampWindow(_floatingWindow, left, top, width, height);
        ClampWindow(_sidebarWindow, left, top, width, height);
    }

    private static void ClampWindow(Window? window, double left, double top, double width, double height)
    {
        if (window is null || !window.IsVisible)
        {
            return;
        }

        double windowWidth = window.Width;
        double windowHeight = window.Height;
        double newLeft = Math.Clamp(window.Left, left, Math.Max(left, left + width - windowWidth));
        double newTop = Math.Clamp(window.Top, top, Math.Max(top, top + height - windowHeight));

        if (!window.Left.Equals(newLeft) || !window.Top.Equals(newTop))
        {
            window.Left = newLeft;
            window.Top = newTop;
        }
    }

    // ---------- 热键 ----------

    private void RegisterHotkeyWithFeedback()
    {
        if (_hotkeyService is null || _mainViewModel is null)
        {
            return;
        }

        HotkeyDefinition definition = _mainViewModel.Workspace.Settings.Hotkey;
        OperationResult result = _hotkeyService.Register(definition);
        if (result.Success)
        {
            _logger?.Log(SyncLogLevel.Info, $"全局快捷键注册成功：{definition.DisplayText}");
            return;
        }

        // 注册失败：明确提示 + 给替代方案，不静默吞掉
        _logger?.Log(SyncLogLevel.Warning, $"全局快捷键注册失败：{definition.DisplayText} -> {result.ErrorMessage}");
        _tray?.ShowBalloon(
            "DeskLanes 快捷键不可用",
            result.ErrorMessage ?? "快捷键注册失败，可从托盘菜单的设置里更换。");
        _dialogService.ShowError(
            $"{result.ErrorMessage}\n\n你可以在托盘菜单的“设置”里更换快捷键，或先用托盘菜单打开悬浮窗。",
            "快捷键注册失败");
    }

    private static List<WatchTarget> BuildWatchTargets(IDiagnosticLogger logger)
    {
        var targets = new List<WatchTarget>();

        string? userDesktop = KnownFolders.TryGetPath(KnownFolders.FOLDERID_Desktop);
        if (string.IsNullOrEmpty(userDesktop))
        {
            logger.Log(SyncLogLevel.Warning, "无法获取用户桌面路径，已跳过该目录的监听。");
        }
        else
        {
            targets.Add(new WatchTarget(userDesktop, ItemOrigin.UserDesktop, "用户桌面"));
        }

        string? publicDesktop = KnownFolders.TryGetPublicDesktopPath();
        if (string.IsNullOrEmpty(publicDesktop))
        {
            logger.Log(SyncLogLevel.Warning, "无法获取公共桌面路径，已跳过该目录的监听。");
        }
        else
        {
            targets.Add(new WatchTarget(publicDesktop, ItemOrigin.PublicDesktop, "公共桌面"));
        }

        return targets;
    }
}
