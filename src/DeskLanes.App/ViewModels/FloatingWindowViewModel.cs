using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 悬浮窗（快速启动）视图模型。
/// 与全屏、侧边栏共用同一份 <see cref="IWorkspaceService"/> 数据与 <see cref="ShellState"/> 搜索词，
/// 不持有任何项目副本：项目始终来自工作区，图标由 <see cref="IIconService"/> 统一填充。
/// </summary>
public sealed class FloatingWindowViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogService;
    private readonly ILaunchService _launchService;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, DesktopItem> _itemsById = new(StringComparer.Ordinal);

    private FloatingCategoryViewModel? _selectedCategory;
    private string _statusText = "就绪";
    // 阶段 19：悬浮窗两种视图 + 文件夹视图里当前打开的分组
    private FloatingViewMode _viewMode = FloatingViewMode.All;
    private ShortcutGroup? _openedFolder;

    public FloatingWindowViewModel(
        IWorkspaceService workspace,
        IDialogService dialogService,
        ILaunchService launchService,
        ShellState shellState)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _shellState = shellState ?? throw new ArgumentNullException(nameof(shellState));

        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        ToggleSidebarModeCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Sidebar));
        ToggleFullModeCommand = new RelayCommand(_ => ModeSwitchRequested?.Invoke(this, WindowMode.Full));
        HideWindowCommand = new RelayCommand(_ => HideRequested?.Invoke(this, EventArgs.Empty));
        ToggleViewModeCommand = new RelayCommand(_ => ToggleViewMode());
        OpenFolderCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is FloatingFolderCard card)
                {
                    _openedFolder = card.Group;
                    ApplyItems();
                    NotifyViewState();
                }
            },
            parameter => parameter is FloatingFolderCard);
        BackToFoldersCommand = new RelayCommand(_ =>
        {
            _openedFolder = null;
            ApplyItems();
            NotifyViewState();
        });

        _workspace.Changed += (_, _) => Rebuild();
        _shellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyItems();
                OnPropertyChanged(nameof(IsSearchEmpty));
            }
        };

        Rebuild();
    }

    /// <summary>三种模式共用的工作区（悬浮窗不持有任何项目副本）。</summary>
    public IWorkspaceService Workspace => _workspace;

    /// <summary>请求切换到其他窗口模式（悬浮窗/全屏/侧边栏互斥显示）。</summary>
    public event EventHandler<WindowMode>? ModeSwitchRequested;

    /// <summary>请求隐藏悬浮窗（Esc / 快捷键 / 失焦，由视图执行）。</summary>
    public event EventHandler? HideRequested;

    /// <summary>顶部分类标签：全部 + 各用户分类。</summary>
    public ObservableCollection<FloatingCategoryViewModel> Categories { get; } = new();

    /// <summary>当前分类下的项目（按共享搜索词过滤）。</summary>
    public ObservableCollection<DesktopItem> Items { get; } = new();

    /// <summary>「全部」视图里的桌面文件夹（A-Z）。</summary>
    public ObservableCollection<DesktopItem> DesktopFolderItems { get; } = new();

    /// <summary>「全部」视图里的软件/文件（A-Z）。</summary>
    public ObservableCollection<DesktopItem> SoftwareItems { get; } = new();

    /// <summary>「文件夹视图」里的分组卡片。</summary>
    public ObservableCollection<FloatingFolderCard> GroupCards { get; } = new();

    public bool IsAllView => _viewMode == FloatingViewMode.All;

    public bool IsFolderView => _viewMode == FloatingViewMode.Folders;

    public bool IsGroupCardsVisible => IsFolderView && _openedFolder is null;

    public bool IsOpenedFolderVisible => IsFolderView && _openedFolder is not null;

    public bool HasDesktopFolderItems => DesktopFolderItems.Count > 0;

    public bool HasSoftwareItems => SoftwareItems.Count > 0;

    public bool HasGroupCards => GroupCards.Count > 0;

    /// <summary>卡片视图不显示「无项目」空状态。</summary>
    public bool ShowEmptyState => !IsGroupCardsVisible && Items.Count == 0;

    public string ViewModeButtonText => IsAllView ? "文件夹视图" : "全部视图";

    public string OpenedFolderTitle => _openedFolder?.Name ?? string.Empty;

    public RelayCommand ToggleViewModeCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand BackToFoldersCommand { get; }

    public FloatingCategoryViewModel? SelectedCategory
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

    public string SearchText
    {
        get => _shellState.SearchText;
        set
        {
            _shellState.SearchText = value;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsSearchEmpty));
        }
    }

    /// <summary>搜索框是否为空（控制占位水印的显示）。</summary>
    public bool IsSearchEmpty => string.IsNullOrWhiteSpace(_shellState.SearchText);

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => Items.Count == 0;

    public string EmptyStateText => _shellState.IsSearching
        ? $"没有包含“{_shellState.SearchText.Trim()}”的项目。"
        : $"{(_selectedCategory?.Name ?? "当前分类")} 还没有项目。";

    /// <summary>状态栏文字（启动结果 / 项目数）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>失焦时是否自动隐藏（可在设置里关闭）。</summary>
    public bool HideOnDeactivate => _workspace.Settings.FloatingHideOnDeactivate;

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand ToggleSidebarModeCommand { get; }

    public RelayCommand ToggleFullModeCommand { get; }

    public RelayCommand HideWindowCommand { get; }

    /// <summary>双击 / 回车启动项目。</summary>
    private void OpenItem(object? parameter)
    {
        if (parameter is not DesktopItem item)
        {
            return;
        }

        LaunchResult result = _launchService.Launch(item);
        StatusText = result.Success
            ? $"已启动：{item.Name}"
            : (result.ErrorMessage ?? "启动失败。");
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
        Categories.Add(new FloatingCategoryViewModel(null)
        {
            ItemCount = _workspace.Items.Count,   // 「全部」显示真实总数，不要是 0
        });
        foreach (ShortcutGroup group in _workspace.Groups.OrderBy(g => g.Order))
        {
            Categories.Add(new FloatingCategoryViewModel(group));
        }

        _selectedCategory = selectedId is null
            ? Categories.FirstOrDefault()
            : Categories.FirstOrDefault(c => c.GroupId == selectedId) ?? Categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
        ApplyItems();
    }

    private void ToggleViewMode()
    {
        _viewMode = _viewMode == FloatingViewMode.All
            ? FloatingViewMode.Folders
            : FloatingViewMode.All;
        _openedFolder = null;
        ApplyItems();
        NotifyViewState();
    }

    private void NotifyViewState()
    {
        OnPropertyChanged(nameof(IsAllView));
        OnPropertyChanged(nameof(IsFolderView));
        OnPropertyChanged(nameof(IsGroupCardsVisible));
        OnPropertyChanged(nameof(IsOpenedFolderVisible));
        OnPropertyChanged(nameof(ViewModeButtonText));
        OnPropertyChanged(nameof(OpenedFolderTitle));
        OnPropertyChanged(nameof(HasGroupCards));
        OnPropertyChanged(nameof(HasDesktopFolderItems));
        OnPropertyChanged(nameof(HasSoftwareItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ApplyItems()
    {
        Items.Clear();
        DesktopFolderItems.Clear();
        SoftwareItems.Clear();
        GroupCards.Clear();

        string keyword = _shellState.SearchText.Trim();

        // 文件夹视图、还没点开某个分组：只显示分组卡片，不把里面的文件平铺出来。
        if (IsFolderView && _openedFolder is null)
        {
            foreach (ShortcutGroup group in _workspace.Groups
                         .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (keyword.Length == 0 ||
                    group.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    GroupCards.Add(new FloatingFolderCard(group));
                }
            }

            NotifyViewState();
            return;
        }

        IEnumerable<DesktopItem> source;
        if (_openedFolder is not null)
        {
            source = _openedFolder.ItemIds
                .Where(id => _itemsById.ContainsKey(id))
                .Select(id => _itemsById[id]);
        }
        else if (_selectedCategory?.Group is ShortcutGroup selectedGroup)
        {
            source = selectedGroup.ItemIds
                .Where(id => _itemsById.ContainsKey(id))
                .Select(id => _itemsById[id]);
        }
        else
        {
            source = _workspace.Items;   // 「全部」含已分组项目
        }

        if (keyword.Length > 0)
        {
            source = source.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (DesktopItem item in source.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(item);
            (item.Kind == DesktopItemKind.Folder ? DesktopFolderItems : SoftwareItems).Add(item);
        }

        NotifyViewState();
    }

}

/// <summary>悬浮窗的分类标签。<see cref="Group"/> 为 null 表示"全部项目"。</summary>
public sealed class FloatingCategoryViewModel : ObservableObject
{
    public FloatingCategoryViewModel(ShortcutGroup? group)
    {
        Group = group;
        _itemCount = group?.ItemIds.Count ?? 0;
    }

    public ShortcutGroup? Group { get; }

    public string? GroupId => Group?.Id;

    public string Name => Group?.Name ?? "全部";

    private int _itemCount;

    public int ItemCount
    {
        get => _itemCount;
        set => SetProperty(ref _itemCount, value);
    }
}


/// <summary>悬浮窗「文件夹视图」里的一张分组卡片。</summary>
public sealed class FloatingFolderCard
{
    public FloatingFolderCard(ShortcutGroup group) => Group = group;

    public ShortcutGroup Group { get; }

    public string Name => Group.Name;

    public int ItemCount => Group.ItemIds.Count;
}

/// <summary>悬浮窗的两种展示方式。</summary>
public enum FloatingViewMode
{
    /// <summary>全部项目，按「文件夹 / 软件」两区 A-Z 排列。</summary>
    All,

    /// <summary>按用户分组显示文件夹卡片，点开才列出成员。</summary>
    Folders,
}
