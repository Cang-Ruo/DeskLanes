using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskLanes.App.Services;
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
    // 阶段 25：底部「全部」默认收起，点工具栏按钮才弹出
    private bool _isPoolOpen;

    /// <summary>仅供界面快照工具强制展开用；产品运行时为 null，默认收起。</summary>
    public static bool? ForcePoolOpenForSnapshot { get; set; }

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
        ArrangePanelsCommand = new RelayCommand(_ => ArrangePanels());
        TogglePoolCommand = new RelayCommand(_ => IsPoolOpen = !IsPoolOpen);
        RenamePanelCommand = new RelayCommand(_ => RenamePanel(), _ => SelectedPanel is not null);
        DeletePanelCommand = new RelayCommand(_ => DeletePanel(), _ => SelectedPanel is not null);
        MovePanelUpCommand = new RelayCommand(_ => MovePanel(-1), _ => CanMove(-1));
        MovePanelDownCommand = new RelayCommand(_ => MovePanel(1), _ => CanMove(1));
        OpenItemCommand = new RelayCommand(OpenItem, parameter => parameter is DesktopItem);
        RemoveItemCommand = new RelayCommand(RemoveItem, parameter => parameter is DesktopItem);

        _workspace.Changed += (_, _) => Rebuild();
        DragState.Changed += OnDragStateChanged;
        _shellState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellState.SearchText))
            {
                ApplyPool();
            }
        };

        if (ForcePoolOpenForSnapshot == true)
        {
            _isPoolOpen = true;
        }

        Rebuild();
    }

    /// <summary>画布上的分区面板（按 Order 升序）。</summary>
    public ObservableCollection<PanelViewModel> Panels { get; } = new();

    /// <summary>底部「全部」项目（按搜索词过滤，含已归到分区里的）。</summary>
    public ObservableCollection<DesktopItem> PoolItems { get; } = new();

    /// <summary>「全部」里的桌面文件夹（A-Z）。</summary>
    public ObservableCollection<DesktopItem> PoolFolderItems { get; } = new();

    /// <summary>「全部」里的软件/文件（A-Z）。</summary>
    public ObservableCollection<DesktopItem> PoolAppItems { get; } = new();

    public bool HasPoolFolderItems => PoolFolderItems.Count > 0;

    public bool HasPoolAppItems => PoolAppItems.Count > 0;

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

    // 阶段 24：默认高一点，让「软件」「文件夹」两区都能一眼看到；
    // 高度仍可拖动，且底部区是覆盖层，不会压缩上方面布。
    /// <summary>阶段 25：底部「全部」是否展开。默认 false —— 平常只显示上方面布。</summary>
    public bool IsPoolOpen
    {
        get => _isPoolOpen;
        set
        {
            if (SetProperty(ref _isPoolOpen, value))
            {
                OnPropertyChanged(nameof(PoolToggleText));
            }
        }
    }

    public string PoolToggleText => IsPoolOpen ? "收起全部" : "全部";

    /// <summary>可选方格大小（虚拟单位）。</summary>
    public IReadOnlyList<double> GridSizeOptions { get; } = new[] { 20.0, 40.0, 60.0, 80.0 };

    /// <summary>当前方格大小；改动后分区位置/尺寸按新格吸附。</summary>
    public double PanelGridSize
    {
        get => _workspace.Settings.PanelGridSize;
        set
        {
            if (value <= 0 || _workspace.Settings.PanelGridSize == value)
            {
                return;
            }

            _workspace.Settings.PanelGridSize = value;
            _workspace.Save();
            OnPropertyChanged(nameof(PanelGridSize));
        }
    }

    private double _poolPanelHeight = 250;

    /// <summary>阶段 23：底部「全部」区高度，可由上边界的分隔条拖动调整。</summary>
    public double PoolPanelHeight
    {
        get => _poolPanelHeight;
        set
        {
            // 阶段 27：允许一直往上拉到接近铺满内容区，不再卡在 600
            double bounded = Math.Max(120, Math.Min(2000, value));
            SetProperty(ref _poolPanelHeight, bounded);
        }
    }

    public string PoolHeaderText => $"全部（{PoolItems.Count}）";

    public bool HasPoolItems => PoolItems.Count > 0;

    public bool HasNoPoolItems => PoolItems.Count == 0;

    public RelayCommand NewPanelCommand { get; }

    public RelayCommand ArrangePanelsCommand { get; }

    public RelayCommand TogglePoolCommand { get; }

    public RelayCommand RenamePanelCommand { get; }

    public RelayCommand DeletePanelCommand { get; }

    public RelayCommand MovePanelUpCommand { get; }

    public RelayCommand MovePanelDownCommand { get; }

    public RelayCommand OpenItemCommand { get; }

    public RelayCommand RemoveItemCommand { get; }

    /// <summary>
    /// 当前是否有项目正在被拖拽。底部移除区据此显隐：
    /// 平时藏起来，拖起来才出现，避免一条红色横条常驻抢戏。
    /// </summary>
    public bool IsDraggingItem => DragState.IsDragging;

    private void OnDragStateChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(IsDraggingItem));

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

    /// <summary>
    /// 阶段 20：把项目拖到画布空白处时，在落点新建一个分区并把它放进去。
    /// 这样「软件拖到哪里，框就出现在哪里」，不必先手动新建分区。
    /// </summary>
    public void CreatePanelAt(DesktopItem item, double x, double y)
    {
        WorkspacePanel panel = _workspace.CreatePanel("新文件夹");
        double px = Math.Max(0, Math.Min(WorkspacePanel.CanvasWidth - WorkspacePanel.DefaultWidth, x - 40));
        double py = Math.Max(0, Math.Min(WorkspacePanel.CanvasHeight - WorkspacePanel.DefaultHeight, y - 20));
        _workspace.UpdatePanelLayout(panel, px, py, panel.Width, panel.Height);
        _workspace.AssignItemToPanel(item, panel);
        SelectedPanel = Panels.FirstOrDefault(p => p.Model.Id == panel.Id);
    }

    /// <summary>阶段 23：把已有面板按网格重新排一遍，修掉历史上重叠的布局。</summary>
    private void ArrangePanels()
    {
        const double gap = 16;
        double x = WorkspacePanel.DefaultX;
        double y = WorkspacePanel.DefaultY;
        double rowHeight = 0;

        foreach (WorkspacePanel panel in _workspace.Panels.OrderBy(p => p.Order).ToList())
        {
            double w = Math.Max(panel.Width, WorkspacePanel.MinWidth);
            double h = Math.Max(panel.Height, WorkspacePanel.MinHeight);
            if (x + w > WorkspacePanel.CanvasWidth)
            {
                x = WorkspacePanel.DefaultX;
                y += rowHeight + gap;
                rowHeight = 0;
            }

            _workspace.UpdatePanelLayout(panel, x, y, panel.Width, panel.Height);
            x += w + gap;
            rowHeight = Math.Max(rowHeight, h);
        }
    }

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

        PoolFolderItems.Clear();
        PoolAppItems.Clear();

        // 阶段 19：底部改成「全部」，已归到上方分区里的项目也仍然显示。
        string keyword = _shellState.SearchText.Trim();
        IEnumerable<DesktopItem> source = _workspace.Items;
        if (keyword.Length > 0)
        {
            source = source.Where(item =>
                item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        foreach (DesktopItem item in source.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            PoolItems.Add(item);
            (item.Kind == DesktopItemKind.Folder ? PoolFolderItems : PoolAppItems).Add(item);
        }

        OnPropertyChanged(nameof(PoolHeaderText));
        OnPropertyChanged(nameof(HasPoolItems));
        OnPropertyChanged(nameof(HasNoPoolItems));
        OnPropertyChanged(nameof(HasPoolFolderItems));
        OnPropertyChanged(nameof(HasPoolAppItems));
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

    /// <summary>阶段 26：拖动/缩放结束时补一次落盘（过程中已节流）。</summary>
    public void FlushGeometry() => _workspace.Save();

    private void UpdateLayout(double x, double y, double width, double height)
    {
        // 拖拽/缩放高频路径：直接写模型并持久化，不触发 Changed 重建
        _workspace.UpdatePanelLayout(Model, x, y, width, height);
        RefreshBindings();
    }
}
