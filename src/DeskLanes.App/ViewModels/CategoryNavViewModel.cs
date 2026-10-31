using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskLanes.App.Services;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 分类导航式布局的视图模型：左侧分类导航，右侧项目网格。
/// 与自由分区式共用同一份工作区数据，切换布局不丢数据。
/// </summary>
public sealed class CategoryNavViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogService;
    private readonly ILaunchService _launchService;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, DesktopItem> _itemsById;

    private CategoryNodeViewModel? _selectedCategory;

    public CategoryNavViewModel(
        IWorkspaceService workspace,
        IDialogService dialogService,
        ILaunchService launchService,
        ShellState shellState)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _shellState = shellState ?? throw new ArgumentNullException(nameof(shellState));

        _itemsById = new Dictionary<string, DesktopItem>(StringComparer.Ordinal);
        foreach (DesktopItem item in _workspace.Items)
        {
            _itemsById[item.Id] = item;
        }

        NewCategoryCommand = new RelayCommand(_ => CreateCategory());
        RenameCategoryCommand = new RelayCommand(_ => RenameCategory(), _ => SelectedCategory?.Group is not null);
        DeleteCategoryCommand = new RelayCommand(_ => DeleteCategory(), _ => SelectedCategory?.Group is not null);
        MoveCategoryUpCommand = new RelayCommand(_ => MoveCategory(-1), _ => CanMove(-1));
        MoveCategoryDownCommand = new RelayCommand(_ => MoveCategory(1), _ => CanMove(1));
        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        RemoveItemCommand = new RelayCommand(RemoveItem, parameter => parameter is DesktopItem);

        _workspace.Changed += (_, _) => Rebuild();
        DragState.Changed += OnDragStateChanged;
        _shellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyItems();
                OnPropertyChanged(nameof(SearchHint));
            }
        };

        Rebuild();
    }

    /// <summary>分类节点列表（第 0 项为"未分组"，其余为用户分类，按 Order 升序）。</summary>
    public ObservableCollection<CategoryNodeViewModel> Categories { get; } = new();

    /// <summary>当前选中分类的项目（按搜索词过滤）。</summary>
    public ObservableCollection<DesktopItem> Items { get; } = new();

    public CategoryNodeViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyItems();
                RaiseCategoryCommands();
            }
        }
    }

    /// <summary>右侧空状态提示（空分类 / 无搜索结果）。</summary>
    public string EmptyStateText
    {
        get
        {
            if (_selectedCategory is null)
            {
                return "请选择左侧分类查看项目。";
            }

            if (_shellState.IsSearching)
            {
                return $"“{_selectedCategory.Name}”中没有包含“{_shellState.SearchText.Trim()}”的项目。";
            }

            return $"“{_selectedCategory.Name}”还没有项目，把项目拖到这里即可加入。";
        }
    }

    public bool HasNoItems => SelectedCategory is not null && Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public string SearchHint => _shellState.IsSearching
        ? $"在“{_selectedCategory?.Name ?? "全部"}”中搜索“{_shellState.SearchText.Trim()}”"
        : string.Empty;

    public bool HasSearchHint => !string.IsNullOrEmpty(SearchHint);

    public RelayCommand NewCategoryCommand { get; }

    public RelayCommand RenameCategoryCommand { get; }

    public RelayCommand DeleteCategoryCommand { get; }

    public RelayCommand MoveCategoryUpCommand { get; }

    public RelayCommand MoveCategoryDownCommand { get; }

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand RemoveItemCommand { get; }

    /// <summary>
    /// 当前是否有项目正在被拖拽。底部移除区据此显隐：
    /// 平时藏起来，拖起来才出现，避免一条红色横条常驻抢戏。
    /// </summary>
    public bool IsDraggingItem => DragState.IsDragging;

    private void OnDragStateChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(IsDraggingItem));

    /// <summary>把新添加的自定义项目放入当前选中的分类（未分组除外由用户自行拖拽）。</summary>
    public void AssignToSelected(DesktopItem item)
    {
        if (_selectedCategory?.Group is ShortcutGroup group)
        {
            _workspace.AssignItemToGroup(item, group);
        }
    }

    /// <summary>把项目放入指定分类节点（拖拽目标）。</summary>
    public void AssignToNode(DesktopItem item, CategoryNodeViewModel node)
        => _workspace.AssignItemToGroup(item, node.Group);

    /// <summary>按 Id 查找项目（拖拽放置时用）；找不到返回 null。</summary>
    public DesktopItem? FindItemById(string id)
        => _itemsById.TryGetValue(id, out DesktopItem? item) ? item : null;

    private void CreateCategory()
    {
        string? name = _dialogService.Prompt("请输入新分类名称：", "新建分类", "新建分类");
        if (name is null)
        {
            return;
        }

        ShortcutGroup group = _workspace.CreateGroup(name);
        SelectedCategory = Categories.FirstOrDefault(c => c.Group?.Id == group.Id);
    }

    private void RenameCategory()
    {
        if (_selectedCategory?.Group is not ShortcutGroup group)
        {
            return;
        }

        string? name = _dialogService.Prompt("请输入新的分类名称：", "重命名分类", group.Name);
        if (name is null)
        {
            return;
        }

        _workspace.RenameGroup(group, name);
    }

    private void DeleteCategory()
    {
        if (_selectedCategory?.Group is not ShortcutGroup group)
        {
            return;
        }

        bool confirmed = _dialogService.Confirm(
            $"确定删除分类“{group.Name}”吗？\n分类内的项目会回到未分组，桌面文件不会受到任何影响。",
            "删除分类");
        if (!confirmed)
        {
            return;
        }

        _workspace.DeleteGroup(group);
        SelectedCategory = Categories.FirstOrDefault();
    }

    private void MoveCategory(int delta) => _workspace.MoveGroup(_selectedCategory!.Group!, delta);

    private bool CanMove(int delta)
    {
        if (_selectedCategory?.Group is null)
        {
            return false;
        }

        List<CategoryNodeViewModel> movable = Categories.Where(c => c.Group is not null).ToList();
        int index = movable.IndexOf(_selectedCategory);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < movable.Count;
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

    private void RemoveItem(object? parameter)
    {
        if (parameter is not DesktopItem item)
        {
            return;
        }

        bool confirmed = _dialogService.Confirm(
            $"确定把“{item.Name}”从 DeskLanes 移除吗？\n这只会移除工作区里的记录，不会删除或移动桌面上的任何文件。",
            "移除项目");
        if (!confirmed)
        {
            return;
        }

        _workspace.RemoveItem(item);
    }

    private void Rebuild()
    {
        _itemsById.Clear();
        foreach (DesktopItem item in _workspace.Items)
        {
            _itemsById[item.Id] = item;
        }

        string? selectedGroupId = _selectedCategory?.Group?.Id;

        Categories.Clear();
        Categories.Add(new CategoryNodeViewModel(null, CountUngrouped()));
        foreach (ShortcutGroup group in _workspace.Groups.OrderBy(g => g.Order))
        {
            Categories.Add(new CategoryNodeViewModel(group, group.ItemIds.Count));
        }

        CategoryNodeViewModel? reselect = selectedGroupId is null
            ? Categories.FirstOrDefault()
            : Categories.FirstOrDefault(c => c.Group?.Id == selectedGroupId) ?? Categories.FirstOrDefault();

        _selectedCategory = reselect;
        OnPropertyChanged(nameof(SelectedCategory));
        ApplyItems();
        RaiseCategoryCommands();
    }

    private int CountUngrouped()
    {
        HashSet<string> assigned = new(StringComparer.Ordinal);
        foreach (ShortcutGroup group in _workspace.Groups)
        {
            foreach (string id in group.ItemIds)
            {
                assigned.Add(id);
            }
        }

        foreach (WorkspacePanel panel in _workspace.Panels)
        {
            foreach (string id in panel.ItemIds)
            {
                assigned.Add(id);
            }
        }

        return _workspace.Items.Count(i => !assigned.Contains(i.Id));
    }

    private void ApplyItems()
    {
        Items.Clear();

        IEnumerable<DesktopItem> source = _selectedCategory?.Group is ShortcutGroup group
            ? group.ItemIds.Where(id => _itemsById.ContainsKey(id)).Select(id => _itemsById[id])
            : _workspace.Items.Where(IsUngrouped);

        string keyword = _shellState.SearchText.Trim();
        if (keyword.Length > 0)
        {
            source = source.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (DesktopItem item in source)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(HasSearchHint));
    }

    private bool IsUngrouped(DesktopItem item)
    {
        foreach (ShortcutGroup group in _workspace.Groups)
        {
            if (group.ItemIds.Contains(item.Id))
            {
                return false;
            }
        }

        foreach (WorkspacePanel panel in _workspace.Panels)
        {
            if (panel.ItemIds.Contains(item.Id))
            {
                return false;
            }
        }

        return true;
    }

    private void RaiseCategoryCommands()
    {
        RenameCategoryCommand.RaiseCanExecuteChanged();
        DeleteCategoryCommand.RaiseCanExecuteChanged();
        MoveCategoryUpCommand.RaiseCanExecuteChanged();
        MoveCategoryDownCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>分类导航中的一个节点。<see cref="Group"/> 为 null 表示"未分组"。</summary>
public sealed class CategoryNodeViewModel : ObservableObject
{
    public CategoryNodeViewModel(ShortcutGroup? group, int itemCount)
    {
        Group = group;
        _itemCount = itemCount;
    }

    /// <summary>null 表示未分组（虚拟节点，不属于任何用户分类）。</summary>
    public ShortcutGroup? Group { get; }

    public string Name => Group?.Name ?? "未分组";

    public bool IsUngrouped => Group is null;

    private int _itemCount;

    /// <summary>节点内项目数量。</summary>
    public int ItemCount
    {
        get => _itemCount;
        set => SetProperty(ref _itemCount, value);
    }
}
