using System.IO;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;
using DeskLanes.App.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 设置窗口视图模型：窗口模式、全局快捷键、悬浮窗/侧边栏行为、托盘行为。
/// 快捷键修改会先真实注册验证（注册失败时给出中文原因与替代方案），确认后才写入设置。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IHotkeyService _hotkeyService;
    private readonly IDialogService _dialogService;
    private readonly IStartupService? _startupService;
    private readonly IDesktopIconService? _desktopIconService;
    private readonly string _configDir;
    private string _diagnosticsStatus = string.Empty;

    private WindowMode _selectedMode;
    private string _hotkeyText;
    private string _hotkeyStatus = string.Empty;
    private bool _hasHotkeyError;
    private SidebarDock _sidebarDock;
    private double _sidebarWidth;
    private bool _floatingHideOnDeactivate;
    private bool _sidebarAutoCollapse;
    private bool _minimizeToTray;
    private bool _autoStartEnabled;
    private bool _hideDesktopIcons;

    public SettingsViewModel(
        IWorkspaceService workspace,
        IHotkeyService hotkeyService,
        IDialogService dialogService,
        IStartupService? startupService = null,
        IDesktopIconService? desktopIconService = null,
        string? configDir = null)
    {
        _startupService = startupService;
        _desktopIconService = desktopIconService;
        // 诊断包要知道配置与日志在哪；没传就退回默认的 %LocalAppData%\DeskLanes
        _configDir = string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLanes")
            : configDir;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        AppSettings settings = _workspace.Settings;
        _selectedMode = settings.WindowMode;
        _hotkeyText = settings.Hotkey.DisplayText;
        _sidebarDock = settings.SidebarDock;
        _sidebarWidth = settings.SidebarWidth;
        _floatingHideOnDeactivate = settings.FloatingHideOnDeactivate;
        _sidebarAutoCollapse = settings.SidebarAutoCollapse;
        _minimizeToTray = settings.MinimizeToTray;

        // 开机自启：以注册表实际状态为准（用户可能在系统设置里改过），
        // 没有启动服务时退化为按配置显示，不影响其他设置。
        _autoStartEnabled = _startupService is not null ? _startupService.IsEnabled : settings.AutoStartEnabled;

        // 桌面图标：以系统实际状态为准（用户可能在桌面右键菜单里改过）
        _hideDesktopIcons = _desktopIconService is not null ? _desktopIconService.IsHidden : settings.HideDesktopIcons;

        TestHotkeyCommand = new RelayCommand(_ => TestHotkey());
        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => Cancelled?.Invoke(this, EventArgs.Empty));
        CleanupForUninstallCommand = new RelayCommand(_ => CleanupForUninstall());
        ExportDiagnosticsCommand = new RelayCommand(_ => ExportDiagnostics());

        RefreshHotkeyStatus();
    }

    /// <summary>设置已保存（关闭窗口并应用）。</summary>
    public event EventHandler? Saved;

    /// <summary>用户取消设置。</summary>
    public event EventHandler? Cancelled;

    public WindowMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsModeFloating));
                OnPropertyChanged(nameof(IsModeFull));
                OnPropertyChanged(nameof(IsModeSidebar));
            }
        }
    }

    public bool IsModeFloating
    {
        get => _selectedMode == WindowMode.Floating;
        set => SelectMode(WindowMode.Floating, value);
    }

    public bool IsModeFull
    {
        get => _selectedMode == WindowMode.Full;
        set => SelectMode(WindowMode.Full, value);
    }

    public bool IsModeSidebar
    {
        get => _selectedMode == WindowMode.Sidebar;
        set => SelectMode(WindowMode.Sidebar, value);
    }

    private void SelectMode(WindowMode mode, bool selected)
    {
        if (!selected)
        {
            // 单选按钮取消勾选时不需要改变状态
            return;
        }

        SelectedMode = mode;
    }

    /// <summary>快捷键输入文本（形如 "Ctrl+Alt+Space"）。</summary>
    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (SetProperty(ref _hotkeyText, value))
            {
                HasHotkeyError = false;
                HotkeyStatus = string.Empty;
            }
        }
    }

    /// <summary>快捷键注册结果说明。</summary>
    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set
        {
            if (SetProperty(ref _hotkeyStatus, value))
            {
                OnPropertyChanged(nameof(HasHotkeyStatus));
            }
        }
    }

    public bool HasHotkeyStatus => !string.IsNullOrEmpty(HotkeyStatus);

    public bool HasHotkeyError
    {
        get => _hasHotkeyError;
        private set
        {
            if (SetProperty(ref _hasHotkeyError, value))
            {
                OnPropertyChanged(nameof(HasHotkeyError));
            }
        }
    }

    public SidebarDock SidebarDock
    {
        get => _sidebarDock;
        set
        {
            if (SetProperty(ref _sidebarDock, value))
            {
                OnPropertyChanged(nameof(IsDockLeft));
                OnPropertyChanged(nameof(IsDockRight));
            }
        }
    }

    public bool IsDockLeft
    {
        get => _sidebarDock == SidebarDock.Left;
        set
        {
            if (value)
            {
                SidebarDock = SidebarDock.Left;
            }
        }
    }

    public bool IsDockRight
    {
        get => _sidebarDock == SidebarDock.Right;
        set
        {
            if (value)
            {
                SidebarDock = SidebarDock.Right;
            }
        }
    }

    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            // 阶段 28：下限 180 → 100。180 在竖屏 / 小屏上仍嫌太宽，
            // 用户明确要求放开到 100（顶栏会自动换行，内容区文字靠截断 + ToolTip）。
            double bounded = Math.Clamp(value, 100, 520);
            if (SetProperty(ref _sidebarWidth, bounded))
            {
                // 阶段 27：拖滑块时立刻写进共享配置并通知侧栏重算宽度，
                // 不用等点「应用」；真正落盘仍由 ApplySettings 统一处理。
                _workspace.Settings.SidebarWidth = bounded;
                _workspace.NotifySettingsChanged();
            }
        }
    }

    public bool FloatingHideOnDeactivate
    {
        get => _floatingHideOnDeactivate;
        set => SetProperty(ref _floatingHideOnDeactivate, value);
    }

    public bool SidebarAutoCollapse
    {
        get => _sidebarAutoCollapse;
        set => SetProperty(ref _sidebarAutoCollapse, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    /// <summary>是否随 Windows 登录自动启动（写 HKCU Run 启动项）。</summary>
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetProperty(ref _autoStartEnabled, value);
    }

    /// <summary>开机自启的说明/异常提示（无异常时为 null）。</summary>
    public string? AutoStartStatus => _startupService?.StatusText;

    /// <summary>
    /// 没有启动服务、或读取启动项失败（状态未知）时禁用勾选框，
    /// 避免"勾了却不生效"或"在未知状态下覆盖系统设置"。
    /// </summary>
    public bool CanEditAutoStart => _startupService is not null && _startupService.StatusText is null;

    /// <summary>是否隐藏桌面图标。</summary>
    public bool HideDesktopIcons
    {
        get => _hideDesktopIcons;
        set => SetProperty(ref _hideDesktopIcons, value);
    }

    /// <summary>桌面图标服务的说明/异常提示。</summary>
    public string? DesktopIconStatus => _desktopIconService?.StatusText;

    /// <summary>没有服务、或读取系统状态失败（状态未知）时禁用勾选框。</summary>
    public bool CanEditDesktopIcons => _desktopIconService is not null && _desktopIconService.StatusText is null;

    public RelayCommand TestHotkeyCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>卸载 / 移除 DeskLanes 前的系统清理（删除自启项 + 恢复桌面图标）。</summary>
    public RelayCommand CleanupForUninstallCommand { get; }

    /// <summary>导出诊断包（配置 + 日志 + 备份清单），用于跨设备排障。</summary>
    public RelayCommand ExportDiagnosticsCommand { get; }

    /// <summary>最近一次诊断包导出的结果说明（空则不显示）。</summary>
    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set
        {
            if (SetProperty(ref _diagnosticsStatus, value))
            {
                OnPropertyChanged(nameof(HasDiagnosticsStatus));
            }
        }
    }

    public bool HasDiagnosticsStatus => !string.IsNullOrEmpty(DiagnosticsStatus);

    /// <summary>当前已注册的快捷键说明（打开设置时展示）。</summary>
    public string CurrentHotkeyText
        => _hotkeyService.IsRegistered && _hotkeyService.Current is not null
            ? _hotkeyService.Current.DisplayText
            : "未注册";

    private void RefreshHotkeyStatus()
        => OnPropertyChanged(nameof(CurrentHotkeyText));

    /// <summary>试注册用户输入的组合键：成功只做提示，不立即写入设置。</summary>
    private void TestHotkey()
    {
        HotkeyDefinition? definition = HotkeyDefinition.TryParse(HotkeyText);
        if (definition is null)
        {
            HasHotkeyError = true;
            HotkeyStatus = "无法识别这个组合键。请使用 “Ctrl+Alt+空格” 这样的写法，例如 Ctrl+Alt+D、Ctrl+Shift+Space。";
            return;
        }

        OperationResult result = _hotkeyService.Register(definition);
        if (result.Success)
        {
            HasHotkeyError = false;
            HotkeyStatus = $"组合键 {definition.DisplayText} 可以注册，点击“保存”后生效。";
            RefreshHotkeyStatus();
            return;
        }

        HasHotkeyError = true;
        HotkeyStatus = result.ErrorMessage ?? "注册失败。";
    }

    /// <summary>写入设置并应用（快捷键已注册的会立即生效）。</summary>
    private void Save()
    {
        AppSettings settings = _workspace.Settings;

        HotkeyDefinition? definition = HotkeyDefinition.TryParse(HotkeyText);
        if (definition is not null)
        {
            OperationResult result = _hotkeyService.Register(definition);
            if (result.Success)
            {
                settings.HotkeyModifiers = definition.Modifiers;
                settings.HotkeyKey = definition.Key;
            }
            else
            {
                // 注册失败：不覆盖原有快捷键，明确告知用户
                _dialogService.ShowError(
                    $"{result.ErrorMessage}\n\n已保留原快捷键 {CurrentHotkeyText}，未做修改。",
                    "快捷键注册失败");
                return;
            }
        }
        else if (!string.Equals(HotkeyText, settings.Hotkey.DisplayText, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowError(
                "无法识别输入的快捷键，未做修改。", "快捷键无效");
            return;
        }

        settings.WindowMode = SelectedMode;
        settings.SidebarDock = SidebarDock;
        settings.SidebarWidth = SidebarWidth;
        settings.FloatingHideOnDeactivate = FloatingHideOnDeactivate;
        settings.SidebarAutoCollapse = SidebarAutoCollapse;
        settings.MinimizeToTray = MinimizeToTray;

        // 开机自启：先真实写注册表，写失败时明确提示且不把配置记成"已开启"
        if (_startupService is not null)
        {
            OperationResult startupResult = _startupService.SetEnabled(AutoStartEnabled);
            if (!startupResult.Success)
            {
                _dialogService.ShowError(
                    $"{startupResult.ErrorMessage}\n\n开机自启未设置，其余设置不受影响。",
                    "开机自启设置失败");
                settings.AutoStartEnabled = _startupService.IsEnabled;
            }
            else
            {
                settings.AutoStartEnabled = AutoStartEnabled;
            }
        }
        else
        {
            settings.AutoStartEnabled = AutoStartEnabled;
        }

        // 桌面图标：默认关闭 + 每次"启用"都要用户逐次授权（安全红线）
        if (_desktopIconService is not null)
        {
            if (HideDesktopIcons && !_desktopIconService.IsHidden)
            {
                bool authorized = _dialogService.Confirm(
                    "即将隐藏桌面上的所有图标（写入系统“显示桌面图标”设置并立即生效）。\n\n" +
                    "这不会移动、删除或修改任何桌面文件，也不会重启资源管理器；" +
                    "随时可以在这里取消勾选恢复显示。\n\n确定要隐藏桌面图标吗？",
                    "隐藏桌面图标");
                if (!authorized)
                {
                    // 未授权：保持现状，不写系统设置，其余设置继续保存
                    HideDesktopIcons = false;
                    settings.HideDesktopIcons = false;
                }
            }

            if (HideDesktopIcons != _desktopIconService.IsHidden || HideDesktopIcons != settings.HideDesktopIcons)
            {
                OperationResult iconResult = _desktopIconService.SetHidden(HideDesktopIcons);
                if (!iconResult.Success)
                {
                    _dialogService.ShowError(
                        $"{iconResult.ErrorMessage}\n\n桌面图标设置未生效，其余设置不受影响。",
                        "桌面图标设置失败");
                }
                settings.HideDesktopIcons = _desktopIconService.IsHidden;
            }
        }
        else
        {
            settings.HideDesktopIcons = HideDesktopIcons;
        }

        _workspace.Save();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 卸载 / 移除 DeskLanes 前的系统清理：删除自启项、恢复桌面图标，并把配置同步成清理后的状态，
    /// 避免下次启动又把"已启用 / 已隐藏"写回系统。DeskLanes 本身不提供卸载器，这是唯一的清理入口。
    /// </summary>
    private void CleanupForUninstall()
    {
        bool confirmed = _dialogService.Confirm(
            "即将为移除 DeskLanes 做系统清理，共两步：\n\n" +
            "1. 删除当前用户的开机自启启动项（HKCU Run 里的 DeskLanes）。\n" +
            "2. 恢复桌面图标显示（仅当前处于隐藏状态时执行）。\n\n" +
            "不会移动、删除或修改任何桌面文件，也不会重启资源管理器。\n\n确定要执行清理吗？",
            "卸载前清理");
        if (!confirmed)
        {
            return;
        }

        List<string> problems = new();

        if (_startupService is not null)
        {
            OperationResult r = _startupService.SetEnabled(false);
            if (r.Success)
            {
                AutoStartEnabled = false;
            }
            else
            {
                problems.Add("· 删除开机自启失败：" + r.ErrorMessage);
            }
        }

        if (_desktopIconService is not null)
        {
            _desktopIconService.Refresh();
            if (_desktopIconService.IsHidden)
            {
                OperationResult r = _desktopIconService.SetHidden(false);
                if (r.Success)
                {
                    HideDesktopIcons = false;
                }
                else
                {
                    problems.Add("· 恢复桌面图标失败：" + r.ErrorMessage);
                }
            }
        }

        // 把配置同步成清理后的真实状态，避免下次启动把旧行为写回系统
        if (_workspace.Settings.AutoStartEnabled != AutoStartEnabled)
        {
            _workspace.Settings.AutoStartEnabled = AutoStartEnabled;
        }

        if (_workspace.Settings.HideDesktopIcons != HideDesktopIcons)
        {
            _workspace.Settings.HideDesktopIcons = HideDesktopIcons;
        }

        _workspace.Save();

        if (problems.Count > 0)
        {
            _dialogService.ShowError(
                "清理未全部完成：\n" + string.Join("\n", problems) +
                "\n\n你可以手动检查注册表 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 与" +
                "桌面右键菜单里的「查看 → 显示桌面图标」。",
                "卸载前清理");
            return;
        }

        _dialogService.ShowInfo(
            "清理完成：\n· 开机自启已删除\n· 桌面图标已恢复显示\n\n现在可以放心移除 DeskLanes 程序文件。",
            "卸载前清理");
    }

    /// <summary>
    /// 导出诊断包：把 settings.json / library.json / logs\*.log 与备份清单打成一个 zip 放到桌面。
    /// 跨设备排障时这是唯一可靠的现场还原手段；包内含个人文件路径，导出前明确告知用户。
    /// </summary>
    private void ExportDiagnostics()
    {
        bool confirmed = _dialogService.Confirm(
            "即将把以下内容打包成一个 zip 放到桌面：\n\n" +
            "· 设置文件 settings.json\n" +
            "· 项目库 library.json（分类、自由分区与项目关联）\n" +
            "· 日志 logs\\sync.log 与 logs\\errors.log\n" +
            "· 配置备份清单（只记名字，不打包内容）\n\n" +
            "只会读取这些文件，不会修改或删除任何数据。\n" +
            "注意：包里含有你的个人文件路径，发给别人前请先确认。\n\n确定要导出吗？",
            "导出诊断包");
        if (!confirmed)
        {
            return;
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        string zipPath = Path.Combine(desktop, $"DeskLanes-诊断包-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        OperationResult result = new DiagnosticExporter(_configDir).Export(zipPath);
        if (!result.Success)
        {
            DiagnosticsStatus = "导出失败：" + result.ErrorMessage;
            _dialogService.ShowError(result.ErrorMessage ?? "导出诊断包失败。", "导出诊断包");
            return;
        }

        DiagnosticsStatus = "已导出到桌面：" + Path.GetFileName(zipPath);
        _dialogService.ShowInfo(
            "诊断包已导出：\n" + zipPath + "\n\n把它发给帮你排查问题的人即可。",
            "导出诊断包");
    }
}
