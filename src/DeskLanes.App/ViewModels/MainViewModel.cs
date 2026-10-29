using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 主窗口外壳视图模型：扫描调度、布局模式切换、自定义添加、启动分发与状态提示。
/// 具体布局内容委托给 <see cref="CategoryNav"/> 与 <see cref="FreePanels"/>，
/// 两者共用同一份工作区数据，切换布局不丢数据。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IDesktopScanner _scanner;
    private readonly IIconService _iconService;
    private readonly ILaunchService _launchService;
    private readonly IDialogService _dialogService;
    private readonly IWorkspaceService _workspace;
    private readonly IDesktopSyncService? _syncService;
    private readonly List<DesktopItem> _allItems = new();

    private bool _isLoading;
    private string _statusText = "就绪";
    private string? _warningText;
    private string _syncStatusText = "自动同步：未启用";
    private bool _hasSyncWarning;
    private DesktopItem? _selectedItem;
    private LayoutMode _currentLayout;

    public MainViewModel(
        IWorkspaceService workspace,
        IDesktopScanner scanner,
        IIconService iconService,
        ILaunchService launchService,
        IDialogService dialogService,
        ShellState shellState,
        IDesktopSyncService? syncService = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ShellState = shellState ?? throw new ArgumentNullException(nameof(shellState));
        _syncService = syncService;

        _currentLayout = _workspace.Settings.LayoutMode;
        CategoryNav = new CategoryNavViewModel(_workspace, _dialogService, _launchService, ShellState);
        FreePanels = new FreePanelsViewModel(_workspace, _dialogService, _launchService, ShellState);

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsLoading);
        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        DismissWarningCommand = new RelayCommand(_ => WarningText = null);
        ToggleLayoutCommand = new RelayCommand(_ => ToggleLayout());
        SwitchToFloatingCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Floating));
        SwitchToSidebarCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Sidebar));
        AddCustomCommand = new RelayCommand(_ => _ = AddCustomAsync());

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoResults));
        };

        _workspace.Changed += (_, _) =>
        {
            RebuildAllItems();
            UpdateStatusCounts();
            UpdateCurrentView();
        };

        _workspace.SaveFailed += (_, message) => _dialogService.ShowError(message, "保存失败");

        if (_syncService is not null)
        {
            // 自动同步在后台线程完成扫描与合并，这里只把结果应用到界面
            _syncService.SyncCompleted += (_, _) => OnAutoSyncCompleted();
            _syncService.StatusChanged += (_, _) => UpdateSyncStatus();
            UpdateSyncStatus();
        }

        ShellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyFilter();
            }
        };

        UpdateCurrentView();
    }

    public ShellState ShellState { get; }

    /// <summary>工作区服务（窗口偏好等设置读取用）。</summary>
    public IWorkspaceService Workspace => _workspace;

    /// <summary>请求切换到其他窗口模式（悬浮窗 / 侧边栏）。由 App 层的模式管理器执行。</summary>
    public event EventHandler<WindowMode>? ModeSwitchRequested;

    /// <summary>分类导航式视图模型。</summary>
    public CategoryNavViewModel CategoryNav { get; }

    /// <summary>自由分区式视图模型。</summary>
    public FreePanelsViewModel FreePanels { get; }

    /// <summary>当前布局模式对应的视图（绑定到外壳 ContentControl）。</summary>
    public object CurrentView => IsCategoryNav ? CategoryNav : FreePanels;

    public LayoutMode CurrentLayout
    {
        get => _currentLayout;
        private set
        {
            if (SetProperty(ref _currentLayout, value))
            {
                OnPropertyChanged(nameof(IsCategoryNav));
                OnPropertyChanged(nameof(IsFreePanels));
                OnPropertyChanged(nameof(CurrentView));
                OnPropertyChanged(nameof(LayoutToggleText));
            }
        }
    }

    public bool IsCategoryNav => CurrentLayout == LayoutMode.CategoryNav;

    public bool IsFreePanels => CurrentLayout == LayoutMode.FreePanels;

    /// <summary>布局切换按钮文字（点击后切换到另一种模式）。</summary>
    public string LayoutToggleText => IsCategoryNav ? "切换到自由分区" : "切换到分类导航";

    /// <summary>当前展示的项目（全库按搜索词过滤；具体布局由子视图模型渲染）。</summary>
    public ObservableCollection<DesktopItem> Items { get; } = new();

    /// <summary>搜索框文本（与子视图模型共享同一状态，切换布局不丢失）。</summary>
    public string SearchText
    {
        get => ShellState.SearchText;
        set
        {
            ShellState.SearchText = value;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsSearchEmpty));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsNotLoading));
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>加载配置或扫描过程中的警告。</summary>
    public string? WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => WarningText is not null;

    /// <summary>桌面自动同步的状态文字（监听正常 / 降级轮询 / 部分监听）。</summary>
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    /// <summary>自动同步处于降级状态（没有目录被监听）时提示。</summary>
    public bool HasSyncWarning
    {
        get => _hasSyncWarning;
        private set => SetProperty(ref _hasSyncWarning, value);
    }

    /// <summary>状态栏里自动同步部分的悬停说明（最近的诊断日志）。</summary>
    public string SyncStatusDetail => _syncService is null
        ? "自动同步未启用。"
        : string.Join("\n", _syncService.RecentLog
            .Take(8)
            .Select(entry => $"[{entry.Timestamp:HH:mm:ss}] {entry.Message}"));

    public DesktopItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool HasItems => Items.Count > 0;

    public bool HasNoResults => !IsLoading && Items.Count == 0;

    public bool IsSearchEmpty => string.IsNullOrEmpty(SearchText);

    public string EmptyStateText => IsSearchEmpty
        ? "桌面上还没有任何项目。"
        : $"没有找到包含“{SearchText.Trim()}”的项目。";

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand DismissWarningCommand { get; }

    public RelayCommand ToggleLayoutCommand { get; }

    /// <summary>切换到悬浮窗模式（数据共用）。</summary>
    public RelayCommand SwitchToFloatingCommand { get; }

    /// <summary>切换到侧边栏模式（数据共用）。</summary>
    public RelayCommand SwitchToSidebarCommand { get; }

    public RelayCommand AddCustomCommand { get; }

    /// <summary>窗口显示后自动执行首次扫描，并启动桌面自动同步。</summary>
    public async Task InitializeAsync()
    {
        // 配置加载警告（损坏/迁移/版本过新）在启动时提示一次
        if (_workspace.LoadWarnings.Count > 0)
        {
            WarningText = "配置提示：\n" + string.Join("\n", _workspace.LoadWarnings);
        }

        await RefreshAsync();

        // 首次扫描完成后再开始监听：避免启动瞬间的事件与首次扫描相互干扰
        _syncService?.Start();
        UpdateSyncStatus();
    }

    /// <summary>窗口关闭时释放自动同步资源（监听器 / 定时器）。</summary>
    public void Shutdown()
    {
        _syncService?.Dispose();
    }

    /// <summary>重新扫描桌面并合并进工作区（刷新按钮与测试调用）。</summary>
    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        WarningText = null;
        StatusText = "正在扫描桌面…";

        try
        {
            ScanResult result = await _scanner.ScanAsync();
            await _workspace.MergeScanAsync(result);

            if (result.Warnings.Count > 0)
            {
                WarningText = "扫描提示：\n" + string.Join("\n", result.Warnings);
            }

            RebuildAllItems();
            ApplyFilter();
            await LoadIconsAsync();

            UpdateStatusCounts();
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败";
            _dialogService.ShowError(
                $"扫描桌面时发生错误：\n{ex.Message}\n\n你可以点击“刷新”重试。", "扫描失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>保存窗口偏好（窗口关闭/尺寸变化时由视图调用）。</summary>
    public void SaveWindowPreferences(double width, double height, double? left, double? top, bool maximized)
        => _workspace.SaveWindowPreferences(width, height, left, top, maximized);

    /// <summary>把自动同步的结果应用到界面（回到 UI 线程执行，不显示"正在扫描"遮罩）。</summary>
    private void OnAutoSyncCompleted()
    {
        RunOnUiThread(() =>
        {
            RebuildAllItems();
            ApplyFilter();
            UpdateStatusCounts();
            UpdateSyncStatus();
            _ = LoadIconsAsync();
        });
    }

    private void UpdateSyncStatus()
    {
        if (_syncService is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            SyncStatusText = _syncService.StatusText;
            HasSyncWarning = _syncService.IsDegraded;
            OnPropertyChanged(nameof(SyncStatusDetail));
        });
    }

    /// <summary>没有 WPF 调度器时（控制台测试）直接在当前线程执行。</summary>
    private static void RunOnUiThread(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private void ToggleLayout()
    {
        LayoutMode target = IsCategoryNav ? LayoutMode.FreePanels : LayoutMode.CategoryNav;
        _workspace.SetLayoutMode(target);
    }

    private async Task AddCustomAsync()
    {
        string? path = _dialogService.PickFile("选择要添加的项目（程序 / 快捷方式 / 文件）");
        if (path is null)
        {
            return;
        }

        OperationResult result = _workspace.AddCustomItem(path);
        if (!result.Success)
        {
            _dialogService.ShowError(result.ErrorMessage ?? "添加失败。", "添加项目");
            return;
        }

        DesktopItem? added = _workspace.Items.LastOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (added is not null)
        {
            // 新项目纳入当前选中的分类/分区；没有选中则留在未分组/未分区池
            if (IsCategoryNav)
            {
                CategoryNav.AssignToSelected(added);
            }
            else
            {
                FreePanels.AssignToSelected(added);
            }

            await LoadIconsAsync();
        }
    }

    private async Task LoadIconsAsync()
    {
        await Task.Run(() =>
        {
            foreach (DesktopItem item in _workspace.Items)
            {
                if (item.Icon is not null)
                {
                    continue;
                }

                try
                {
                    ImageSource? icon = _iconService.GetIcon(item.FullPath);
                    if (icon is not null)
                    {
                        item.Icon = icon;
                    }
                }
                catch
                {
                    // 图标失败不影响名称展示
                }
            }

            // 每轮加载完就按当前工作区裁剪一次缓存：已经删除/改名/被移出桌面的文件，
            // 图标不会继续占着内存（这是缓存的主要清理手段，容量上限只是兜底）。
            try
            {
                _iconService.PruneExcept(_workspace.Items.Select(i => i.FullPath));
            }
            catch
            {
                // 裁剪失败只影响内存占用，不影响界面
            }
        });
    }

    private void RebuildAllItems()
    {
        _allItems.Clear();
        _allItems.AddRange(_workspace.Items);
    }

    private void UpdateStatusCounts()
    {
        int total = _workspace.Items.Count;
        int broken = _workspace.Items.Count(i => !i.IsAvailable || i.IsBroken);
        int groups = _workspace.Groups.Count;
        int panels = _workspace.Panels.Count;
        DateTime lastScan = _workspace.Settings.LastScanAt;

        StatusText = lastScan == default
            ? $"共 {total} 个项目 · 分类 {groups} 个 · 分区 {panels} 个"
            : $"共 {total} 个项目（失效 {broken}）· 分类 {groups} 个 · 分区 {panels} 个 · 扫描于 {lastScan:HH:mm:ss}";
    }

    private void UpdateCurrentView()
    {
        CurrentLayout = _workspace.Settings.LayoutMode;
    }

    private void ApplyFilter()
    {
        string keyword = SearchText.Trim();
        IEnumerable<DesktopItem> filtered = _allItems;

        if (keyword.Length > 0)
        {
            filtered = _allItems.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        Items.Clear();
        foreach (DesktopItem item in filtered)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void OpenItem(object? parameter)
    {
        if (parameter is not DesktopItem item)
        {
            return;
        }

        LaunchResult result = _launchService.Launch(item);
        if (!result.Success)
        {
            _dialogService.ShowError(result.ErrorMessage ?? "启动失败。", "无法打开项目");
        }
    }
}
