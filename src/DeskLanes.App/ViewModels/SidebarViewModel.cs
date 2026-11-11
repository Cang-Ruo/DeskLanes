using System.Collections.ObjectModel;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 侧边栏视图模型：停靠在屏幕左侧或右侧的快捷入口栏。
/// 与悬浮窗、全屏共用同一份工作区数据；只保存"显示状态"（停靠侧、是否展开、当前分类）。
/// </summary>
public sealed class SidebarViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogService;
    private readonly ILaunchService _launchService;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, DesktopItem> _itemsById = new(StringComparer.Ordinal);

    private SidebarCategoryViewModel? _selectedCategory;
    private bool _isExpanded;
    private string _statusText = string.Empty;

    public SidebarViewModel(
        IWorkspaceService workspace,
        IDialogService dialogService,
        ILaunchService launchService,
        ShellState shellState)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _shellState = shellState ?? throw new ArgumentNullException(nameof(shellState));

        _isExpanded = _workspace.Settings.SidebarExpanded;

        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        ToggleExpandedCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        ToggleDockCommand = new RelayCommand(_ => ToggleDock());
        ToggleFullModeCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Full));
        ToggleFloatingModeCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Floating));
        HideWindowCommand = new RelayCommand(_ => HideRequested?.Invoke(this, EventArgs.Empty));

        _workspace.Changed += (_, _) => Rebuild();
        _shellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyItems();
            }
        };

        Rebuild();
    }

    /// <summary>请求切换到其他窗口模式。</summary>
    public event EventHandler<WindowMode>? ModeSwitchRequested;

    /// <summary>请求隐藏侧边栏。</summary>
    public event EventHandler? HideRequested;

    /// <summary>侧边栏要显示的分类（可切换；与全屏的分类数据同源）。</summary>
    public ObservableCollection<SidebarCategoryViewModel> Categories { get; } = new();

    /// <summary>当前分类中的项目（按共享搜索词过滤）。</summary>
    public ObservableCollection<DesktopItem> Items { get; } = new();

    /// <summary>阶段 21：软件区（在上）与文件夹区（在下）。</summary>
    public ObservableCollection<DesktopItem> SoftwareItems { get; } = new();

    public ObservableCollection<DesktopItem> FolderItems { get; } = new();

    public bool HasSoftwareItems => SoftwareItems.Count > 0;

    public bool HasFolderItems => FolderItems.Count > 0;

    public SidebarCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyItems();
            }
        }
    }

    /// <summary>是否展开（收起时只保留窄条，仍可点击展开）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsCollapsed));
                OnPropertyChanged(nameof(ExpandToggleText));
                _workspace.Settings.SidebarExpanded = value;
                _workspace.Save();
            }
        }
    }

    public bool IsCollapsed => !IsExpanded;

    /// <summary>展开时的宽度（来自设置）。</summary>
    public double ExpandedWidth => _workspace.Settings.SidebarWidth;

    /// <summary>展开/收起按钮文字。</summary>
    public string ExpandToggleText => IsExpanded ? "收起" : "展开";

    public bool IsDockedRight => _workspace.Settings.SidebarDock == SidebarDock.Right;

    public bool IsDockedLeft => !IsDockedRight;

    /// <summary>停靠侧文字（按钮显示点击后的效果）。</summary>
    public string DockToggleText => IsDockedRight ? "停靠到左侧" : "停靠到右侧";

    /// <summary>失焦自动收起（可在设置里关闭，避免操作过程中意外消失）。</summary>
    public bool AutoCollapseOnFocusLost
    {
        get => _workspace.Settings.SidebarAutoCollapse;
        set
        {
            if (_workspace.Settings.SidebarAutoCollapse == value)
            {
                return;
            }

            _workspace.Settings.SidebarAutoCollapse = value;
            _workspace.Save();
            OnPropertyChanged(nameof(AutoCollapseOnFocusLost));
        }
    }

    public bool HasItems => Items.Count > 0;

    /// <summary>与 HasItems 互补：空状态文字的可见性绑定（悬浮窗 VM 同名同义）。</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>
    /// 空状态文案。
    /// 阶段 20：侧边栏第一个节点固定是「全部」；没有用户分类时就只显示它。
    /// </summary>
    public string EmptyStateText
    {
        get
        {
            if (_shellState.IsSearching)
            {
                return $"没有包含“{_shellState.SearchText.Trim()}”的项目。";
            }

            return "没有项目。\n桌面上的项目会自动出现在这里。";
        }
    }

    /// <summary>阶段 23：平时不显示状态栏，只有启动失败等真正需要提示时才出现。</summary>
    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand ToggleExpandedCommand { get; }

    public RelayCommand ToggleDockCommand { get; }

    public RelayCommand ToggleFullModeCommand { get; }

    public RelayCommand ToggleFloatingModeCommand { get; }

    public RelayCommand HideWindowCommand { get; }

    /// <summary>窗口失焦时调用：仅在开启"失焦收起"且处于展开状态时收起。</summary>
    public void OnWindowDeactivated()
    {
        if (AutoCollapseOnFocusLost && IsExpanded)
        {
            IsExpanded = false;
        }
    }

    private void ToggleDock()
    {
        _workspace.Settings.SidebarDock = _workspace.Settings.SidebarDock == SidebarDock.Right
            ? SidebarDock.Left
            : SidebarDock.Right;
        _workspace.Save();
        OnPropertyChanged(nameof(IsDockedRight));
        OnPropertyChanged(nameof(IsDockedLeft));
        OnPropertyChanged(nameof(DockToggleText));
    }

    private void OpenItem(object? parameter)
    {
        if (parameter is not DesktopItem item)
        {
            return;
        }

        LaunchResult result = _launchService.Launch(item);
        // 阶段 23：启动成功不提示，失败才提示。
        StatusText = result.Success ? string.Empty : (result.ErrorMessage ?? "启动失败。");
    }

    private void Rebuild()
    {
        _itemsById.Clear();
        foreach (DesktopItem item in _workspace.Items)
        {
            _itemsById[item.Id] = item;
        }

        string? selectedId = _selectedCategory?.GroupId;
        Categories.Clear();
        // 阶段 20：「全部」固定占第一位，后面才是用户分类（游戏、其他…）
        Categories.Add(new SidebarCategoryViewModel(null, _workspace.Items.Count));
        foreach (ShortcutGroup group in _workspace.Groups.OrderBy(g => g.Order))
        {
            Categories.Add(new SidebarCategoryViewModel(group));
        }

        _selectedCategory = selectedId is null
            ? Categories.FirstOrDefault()
            : Categories.FirstOrDefault(c => c.GroupId == selectedId) ?? Categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
        ApplyItems();
    }

    private void ApplyItems()
    {
        Items.Clear();
        SoftwareItems.Clear();
        FolderItems.Clear();

        IEnumerable<DesktopItem> source = _selectedCategory?.Group is ShortcutGroup group
            ? group.ItemIds.Where(id => _itemsById.ContainsKey(id)).Select(id => _itemsById[id])
            : _workspace.Items;   // 「全部」显示所有项目

        string keyword = _shellState.SearchText.Trim();
        if (keyword.Length > 0)
        {
            source = source.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (DesktopItem item in source.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(item);
            (item.Kind == DesktopItemKind.Folder ? FolderItems : SoftwareItems).Add(item);
        }

        OnPropertyChanged(nameof(HasSoftwareItems));
        OnPropertyChanged(nameof(HasFolderItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(EmptyStateText));
    }
}

/// <summary>侧边栏的分类选项。</summary>
public sealed class SidebarCategoryViewModel : ObservableObject
{
    private readonly int _allCount;

    public SidebarCategoryViewModel(ShortcutGroup? group, int allCount = 0)
    {
        Group = group;   // null 表示「全部」
        _allCount = allCount;
    }

    public ShortcutGroup? Group { get; }

    public string? GroupId => Group?.Id;

    public string Name => Group?.Name ?? "全部";

    public int ItemCount => Group?.ItemIds.Count ?? _allCount;
}
