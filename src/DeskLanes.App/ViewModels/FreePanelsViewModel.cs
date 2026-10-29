using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskLanes.Core.Infrastructure;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 自由分区式布局的视图模型：虚拟画布上的可拖拽/缩放面板 + 底部未分区项目池。
/// 与分类导航式共用同一份工作区数据，切换布局不丢数据。
/// </summary>
public sealed class FreePanelsViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogService;
    private readonly ILaunchService _launchService;
    private readonly ShellState _shellState;
    private readonly Dictionary<string, DesktopItem> _itemsById;

    private PanelViewModel? _selectedPanel;

    public FreePanelsViewModel(
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

        NewPanelCommand = new RelayCommand(_ => CreatePanel());
        RenamePanelCommand = new RelayCommand(_ => RenamePanel(), _ => SelectedPanel is not null);
        DeletePanelCommand = new RelayCommand(_ => DeletePanel(), _ => SelectedPanel is not null);
        MovePanelUpCommand = new RelayCommand(_ => MovePanel(-1), _ => CanMove(-1));
        MovePanelDownCommand = new RelayCommand(_ => MovePanel(1), _ => CanMove(1));
        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        RemoveItemCommand = new RelayCommand(RemoveItem, parameter => parameter is DesktopItem);

        _workspace.Changed += (_, _) => Rebuild();
        _shellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyPool();
            }
        };

        Rebuild();
    }

    /// <summary>画布上的分区面板（按 Order 升序）。</summary>
    public ObservableCollection<PanelViewModel> Panels { get; } = new();

    /// <summary>未分区项目池（按搜索词过滤）。</summary>
    public ObservableCollection<DesktopItem> PoolItems { get; } = new();

    public PanelViewModel? SelectedPanel
    {
        get => _selectedPanel;
        set
        {
            if (SetProperty(ref _selectedPanel, value))
            {
                RaisePanelCommands();
                OnPropertyChanged(nameof(SelectedPanelTitle));
            }
        }
    }

    public string SelectedPanelTitle => SelectedPanel?.Title ?? string.Empty;

    public string PoolHeaderText => $"未分区项目（{PoolItems.Count}）";

    public bool HasPoolItems => PoolItems.Count > 0;

    public bool HasNoPoolItems => PoolItems.Count == 0;

    public RelayCommand NewPanelCommand { get; }

    public RelayCommand RenamePanelCommand { get; }

    public RelayCommand DeletePanelCommand { get; }

    public RelayCommand MovePanelUpCommand { get; }

    public RelayCommand MovePanelDownCommand { get; }

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand RemoveItemCommand { get; }

    /// <summary>把新添加的自定义项目放入当前选中的分区。</summary>
    public void AssignToSelected(DesktopItem item)
    {
        if (_selectedPanel is not null)
        {
            _workspace.AssignItemToPanel(item, _selectedPanel.Model);
        }
    }

    /// <summary>把项目放入指定分区（拖拽目标）。</summary>
    public void AssignToPanel(DesktopItem item, PanelViewModel panel)
        => _workspace.AssignItemToPanel(item, panel.Model);

    /// <summary>把项目放回未分区池（拖到池中）。</summary>
    public void AssignToPool(DesktopItem item)
        => _workspace.AssignItemToPanel(item, null);

    /// <summary>按 Id 查找项目（拖拽放置时用）；找不到返回 null。</summary>
    public DesktopItem? FindItemById(string id)
        => _itemsById.TryGetValue(id, out DesktopItem? item) ? item : null;

    private void CreatePanel()
    {
        string? title = _dialogService.Prompt("请输入新分区名称：", "新建分区", "新分区");
        if (title is null)
        {
            return;
        }

        WorkspacePanel panel = _workspace.CreatePanel(title);
        SelectedPanel = Panels.FirstOrDefault(p => p.Model.Id == panel.Id);
    }

    private void RenamePanel()
    {
        if (_selectedPanel is null)
        {
            return;
        }

        string? title = _dialogService.Prompt("请输入新的分区名称：", "重命名分区", _selectedPanel.Title);
        if (title is null)
        {
            return;
        }

        _workspace.RenamePanel(_selectedPanel.Model, title);
    }

    private void DeletePanel()
    {
        if (_selectedPanel is null)
        {
            return;
        }

        bool confirmed = _dialogService.Confirm(
            $"确定删除分区“{_selectedPanel.Title}”吗？\n分区内的项目会回到未分区池，桌面文件不会受到任何影响。",
            "删除分区");
        if (!confirmed)
        {
            return;
        }

        _workspace.DeletePanel(_selectedPanel.Model);
        SelectedPanel = null;
    }

    private void MovePanel(int delta) => _workspace.MovePanel(_selectedPanel!.Model, delta);

    private bool CanMove(int delta)
    {
        if (_selectedPanel is null)
        {
            return false;
        }

        int index = Panels.IndexOf(_selectedPanel);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < Panels.Count;
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

        // 稳定实例策略：PanelViewModel 按模型 Id 缓存复用，不整体 Clear 重建，
        // 避免 ItemsControl 频繁销毁/重建容器导致的渲染错位与选中丢失。
        List<WorkspacePanel> ordered = _workspace.Panels.OrderBy(p => p.Order).ToList();

        // 移除已删除的分区
        for (int i = Panels.Count - 1; i >= 0; i--)
        {
            if (!ordered.Any(p => p.Id == Panels[i].Model.Id))
            {
                Panels.RemoveAt(i);
            }
        }

        // 新增 + 排序对齐
        for (int target = 0; target < ordered.Count; target++)
        {
            PanelViewModel? existing = Panels.FirstOrDefault(p => p.Model.Id == ordered[target].Id);
            if (existing is null)
            {
                existing = new PanelViewModel(_workspace, ordered[target]);
                Panels.Insert(Math.Min(target, Panels.Count), existing);
            }

            int current = Panels.IndexOf(existing);
            if (current != target)
            {
                Panels.Move(current, target);
            }
        }

        // 刷新每个面板的内容与绑定
        foreach (PanelViewModel panelVm in Panels)
        {
            panelVm.RebuildItems(_itemsById);
            panelVm.RefreshBindings();
        }

        // 选中项跟随（按 Id 重新指向稳定实例）
        if (_selectedPanel is not null && !Panels.Contains(_selectedPanel))
        {
            PanelViewModel? reselect = Panels.FirstOrDefault(p => p.Model.Id == _selectedPanel.Model.Id);
            _selectedPanel = reselect;
            OnPropertyChanged(nameof(SelectedPanel));
            OnPropertyChanged(nameof(SelectedPanelTitle));
        }

        ApplyPool();
        RaisePanelCommands();
    }

    private void ApplyPool()
    {
        PoolItems.Clear();

        HashSet<string> assigned = new(StringComparer.Ordinal);
        foreach (WorkspacePanel panel in _workspace.Panels)
        {
            foreach (string id in panel.ItemIds)
            {
                assigned.Add(id);
            }
        }

        string keyword = _shellState.SearchText.Trim();
        IEnumerable<DesktopItem> source = _workspace.Items.Where(i => !assigned.Contains(i.Id));
        if (keyword.Length > 0)
        {
            source = source.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (DesktopItem item in source)
        {
            PoolItems.Add(item);
        }

        OnPropertyChanged(nameof(PoolHeaderText));
        OnPropertyChanged(nameof(HasPoolItems));
        OnPropertyChanged(nameof(HasNoPoolItems));
    }

    private void RaisePanelCommands()
    {
        RenamePanelCommand.RaiseCanExecuteChanged();
        DeletePanelCommand.RaiseCanExecuteChanged();
        MovePanelUpCommand.RaiseCanExecuteChanged();
        MovePanelDownCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>单个分区面板的视图模型。几何属性直接映射到模型（虚拟画布坐标）。</summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;

    public PanelViewModel(IWorkspaceService workspace, WorkspacePanel model)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Model = model ?? throw new ArgumentNullException(nameof(model));

        ReturnToPoolCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is DesktopItem item)
                {
                    _workspace.AssignItemToPanel(item, null);
                }
            },
            parameter => parameter is DesktopItem);
    }

    public WorkspacePanel Model { get; }

    public string Title
    {
        get => Model.Title;
        set
        {
            if (Model.Title != value)
            {
                Model.Title = value;
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public double X
    {
        get => Model.X;
        set => UpdateLayout(value, Y, Width, Height);
    }

    public double Y
    {
        get => Model.Y;
        set => UpdateLayout(X, value, Width, Height);
    }

    public double Width
    {
        get => Model.Width;
        set => UpdateLayout(X, Y, value, Height);
    }

    public double Height
    {
        get => Model.Height;
        set => UpdateLayout(X, Y, Width, value);
    }

    /// <summary>面板内的项目。</summary>
    public ObservableCollection<DesktopItem> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>面板内项目数量（标题栏显示）。</summary>
    public int ItemCount => Items.Count;

    public string EmptyHint => "把项目拖到这里";

    /// <summary>把项目放回未分区池（右键菜单）。</summary>
    public RelayCommand ReturnToPoolCommand { get; }

    /// <summary>供视图在模型被外部修改（如加载配置）后刷新绑定值。</summary>
    public void RefreshBindings()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    public void RebuildItems(Dictionary<string, DesktopItem> itemsById)
    {
        Items.Clear();
        foreach (string id in Model.ItemIds)
        {
            if (itemsById.TryGetValue(id, out DesktopItem? item))
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ItemCount));
    }

    private void UpdateLayout(double x, double y, double width, double height)
    {
        // 拖拽/缩放高频路径：直接写模型并持久化，不触发 Changed 重建
        _workspace.UpdatePanelLayout(Model, x, y, width, height);
        RefreshBindings();
    }
}
