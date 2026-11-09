using System.Collections.ObjectModel;
using System.IO;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>
/// 工作区服务实现。所有变更方法都必须在 UI 线程调用（集合变更安全）；
/// 扫描合并前先 await 扫描任务，await 后自动回到 UI 线程。
/// </summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly LibraryStore _libraryStore;
    private readonly ConfigStore _configStore;
    private readonly IShortcutResolver _shortcutResolver;

    public WorkspaceService(
        LibraryStore libraryStore,
        ConfigStore configStore,
        IShortcutResolver shortcutResolver)
    {
        _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _shortcutResolver = shortcutResolver ?? throw new ArgumentNullException(nameof(shortcutResolver));

        _libraryStore.Load();
        _configStore.Load();

        var loadWarnings = new List<string>();
        loadWarnings.AddRange(_libraryStore.Warnings);
        loadWarnings.AddRange(_configStore.Warnings);
        LoadWarnings = loadWarnings;

        // 加载后保证排序稳定（防止手工编辑的配置顺序错乱）
        SortByOrder(Groups, g => g.Order);
        SortByOrder(Panels, p => p.Order);

        // 阶段 19：旧配置里分类与分区是两套独立数据（例如同名「游戏」分类里
        // 有 2 个项目、分区里却是 0 个）。加载后立刻配对并同步成员，不丢用户数据。
        // 这里不补建缺失的一边，避免改变已有集合数量；切换布局时才补全。
        EnsurePairedContainers(createMissing: false);
    }

    private LibraryData Library => _libraryStore.Data;

    private AppSettings SettingsData => _configStore.Data;

    public ObservableCollection<DesktopItem> Items => Library.Items;

    public ObservableCollection<ShortcutGroup> Groups => Library.Groups;

    public ObservableCollection<WorkspacePanel> Panels => Library.Panels;

    public AppSettings Settings => SettingsData;

    public IReadOnlyList<string> LoadWarnings { get; }

    public event EventHandler? Changed;

    /// <summary>保存失败（磁盘错误/权限问题）时触发，参数为中文说明。</summary>
    public event EventHandler<string>? SaveFailed;

    // ---------- 扫描合并 ----------

    public Task<ScanMergeOutcome> MergeScanAsync(ScanResult scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);

        // 目录不可访问（一个项目都没扫到且有警告）时不做合并：
        // 桌面临时不可访问不能把全部项目误判为失效
        if (scan.ScopeDirectories.Count == 0 && scan.Items.Count == 0 && scan.Warnings.Count > 0)
        {
            return Task.FromResult(ScanMergeOutcome.Skip(
                "扫描目录不可访问且未获得任何项目，已跳过本次合并以避免误判失效"));
        }

        var now = DateTime.Now;
        int added = 0;
        int updated = 0;

        var existingByKey = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopItem item in Items)
        {
            existingByKey[item.PathKey] = item;
        }

        foreach (DesktopItem scanned in scan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingByKey.TryGetValue(scanned.PathKey, out DesktopItem? existing))
            {
                // 同一路径：复用稳定 Id 与成员关系，只刷新事实信息
                ApplyFacts(existing, scanned, now);
                updated++;
                existingByKey.Remove(scanned.PathKey);
                continue;
            }

            // 新出现的桌面项目：用户主动忽略过的不再拉回
            if (IsDismissed(scanned.PathKey))
            {
                continue;
            }

            scanned.MarkSeen(now);
            Items.Add(scanned);
            added++;
        }

        // 阶段 14：桌面上确实已经不存在的项目自动清除（用户明确要求，不再需要手动删）。
        // 只在"文件确实不存在、且它所在目录仍可访问"时才删；目录整体不可访问时
        // 只标记失效，避免桌面临时掉线把整个库清空。
        (int marked, int removed) = ReconcileMissing(existingByKey.Values, scan.ScopeDirectories);

        SettingsData.LastScanAt = now;
        Save();
        OnChanged();
        return Task.FromResult(new ScanMergeOutcome(added, updated, marked, false, null, removed));
    }

    /// <summary>
    /// 应用可靠的路径迁移（重命名/在同目录内移动）。只有操作系统明确给出"旧路径 → 新路径"
    /// 时才迁移，复用同一个 Id 与成员关系；目标路径已存在或找不到源项目时不做任何猜测。
    /// 返回实际迁移成功的数量。
    /// </summary>
    public int ApplyPathMigrations(IReadOnlyList<PathMigration> migrations)
    {
        if (migrations is null || migrations.Count == 0)
        {
            return 0;
        }

        var now = DateTime.Now;
        int applied = 0;

        foreach (PathMigration migration in migrations)
        {
            if (string.IsNullOrWhiteSpace(migration.OldPath) || string.IsNullOrWhiteSpace(migration.NewPath))
            {
                continue;
            }

            string oldKey = migration.OldPath.ToUpperInvariant();
            string newKey = migration.NewPath.ToUpperInvariant();
            if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                continue;
            }

            DesktopItem? source = Items.FirstOrDefault(i =>
                string.Equals(i.PathKey, oldKey, StringComparison.Ordinal));
            if (source is null)
            {
                // 源项目不在库中（可能已被用户移除）：不做任何处理
                continue;
            }

            bool targetOccupied = Items.Any(i =>
                string.Equals(i.PathKey, newKey, StringComparison.Ordinal));
            if (targetOccupied)
            {
                // 新路径已经被另一个项目占用：无法确定归属，绝不猜测合并
                continue;
            }

            DesktopItem fresh = DesktopItemFactory.Create(migration.NewPath, source.Origin, _shortcutResolver);
            source.FullPath = fresh.FullPath;
            source.Name = fresh.Name;
            source.Kind = fresh.Kind;
            source.ShortcutTarget = fresh.ShortcutTarget;
            source.ShortcutTargetExists = fresh.ShortcutTargetExists;
            source.ShortcutError = fresh.ShortcutError;
            source.MarkSeen(now);
            applied++;
        }

        if (applied > 0)
        {
            Save();
            OnChanged();
        }

        return applied;
    }

    /// <summary>把扫描到的事实信息刷新到既有项目上（保留 Id / 自定义来源标记 / 成员关系）。</summary>
    private static void ApplyFacts(DesktopItem existing, DesktopItem scanned, DateTime now)
    {
        bool keepCustom = existing.Origin == ItemOrigin.Custom;
        existing.Name = scanned.Name;
        existing.FullPath = scanned.FullPath;
        existing.Kind = scanned.Kind;
        existing.ShortcutTarget = scanned.ShortcutTarget;
        existing.ShortcutTargetExists = scanned.ShortcutTargetExists;
        existing.ShortcutError = scanned.ShortcutError;
        if (!keepCustom)
        {
            existing.Origin = scanned.Origin;
        }

        existing.MarkSeen(now);
    }

    /// <summary>
    /// 标记"扫描中消失"的项目为失效。只有落在本次扫描范围内的桌面项目才参与判定——
    /// 目录没被扫到（临时不可访问 / 局部重扫未覆盖）时不改变其可用状态；
    /// 自定义项不在桌面上是常态，按磁盘实际存在性校正，绝不因为"不在桌面上"就判失效。
    /// </summary>
    /// <summary>
    /// 处理"本次扫描没见到"的旧项目。阶段 14：桌面来源项若文件确实不存在就自动清除，
    /// 不再只是标记失效让用户手动删；但父目录都不可访问时只标记失效，防止桌面临时
    /// 掉线把整个库清空。自定义项保留原来的"按磁盘存在性校正"语义，不自动删除。
    /// </summary>
    private (int Marked, int Removed) ReconcileMissing(
        IEnumerable<DesktopItem> leftovers, IReadOnlyList<string> scopeDirectories)
    {
        var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in scopeDirectories)
        {
            string? normalized = NormalizeDirectory(directory);
            if (normalized is not null)
            {
                scope.Add(normalized);
            }
        }

        int marked = 0;
        int removed = 0;
        foreach (DesktopItem item in leftovers.ToList())
        {
            bool inScope = scope.Count == 0
                // 未声明扫描范围（兼容旧调用/全量扫描）：桌面来源项按"扫描未见即失效"
                ? item.Origin != ItemOrigin.Custom
                : IsDirectoryInScope(item.FullPath, scope);

            // 不在本次扫描范围内：桌面来源项保持原状；自定义项按磁盘存在性校正。
            if (!inScope)
            {
                if (item.Origin != ItemOrigin.Custom)
                {
                    continue;
                }

                bool customExists = ExistsOnDisk(item.FullPath);
                if (item.IsAvailable != customExists)
                {
                    item.IsAvailable = customExists;
                    if (!customExists)
                    {
                        marked++;
                    }
                }

                continue;
            }

            // 自定义项：只校正可用性，不自动删除（用户手动加的东西不该被静默清掉）
            if (item.Origin == ItemOrigin.Custom)
            {
                bool exists = ExistsOnDisk(item.FullPath);
                if (item.IsAvailable != exists)
                {
                    item.IsAvailable = exists;
                    if (!exists)
                    {
                        marked++;
                    }
                }

                continue;
            }

            string? parent = null;
            try
            {
                parent = Path.GetDirectoryName(item.FullPath);
            }
            catch
            {
                // 路径非法：按"无法确认"处理，不删
            }

            bool parentReachable = !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
            bool itemExists = ExistsOnDisk(item.FullPath);

            if (!itemExists && parentReachable)
            {
                // 不写 DismissedPaths：以后用户把这个软件放回桌面，还能自动加回来。
                // DetachFromAllContainers 会清掉它在分类 / 面板里的归属。
                DetachFromAllContainers(item.Id);
                Items.Remove(item);
                removed++;
                continue;
            }

            // 文件还在（本次没扫到）或目录整体不可访问：保留并标记失效
            if (item.IsAvailable)
            {
                item.IsAvailable = false;
                marked++;
            }
        }

        return (marked, removed);
    }

    private static bool IsDirectoryInScope(string fullPath, HashSet<string> scope)
    {
        string? directory = NormalizeDirectory(Path.GetDirectoryName(fullPath));
        return directory is not null && scope.Contains(directory);
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\', '/').ToUpperInvariant();
        }
    }

    private static bool ExistsOnDisk(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            // 路径非法或磁盘不可用时无法确定：按"仍存在"处理，避免误标失效
            return true;
        }
    }

    // ---------- 自定义添加 / 移除 ----------

    public OperationResult AddCustomItem(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return OperationResult.Fail("路径不能为空。");
        }

        string path = fullPath.Trim().Trim('"');
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return OperationResult.Fail($"路径不存在：{path}");
        }

        string key = path.ToUpperInvariant();
        DesktopItem? existing = Items.FirstOrDefault(i =>
            string.Equals(i.PathKey, key, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // 已在库中（含此前被移除的扫描项被重新添加）：解除忽略并恢复
            Undismiss(key);
            if (!existing.IsAvailable)
            {
                existing.IsAvailable = true;
            }

            existing.MarkSeen(DateTime.Now);
            Save();
            OnChanged();
            return OperationResult.Ok();
        }

        DesktopItem item = DesktopItemFactory.Create(path, ItemOrigin.Custom, _shortcutResolver);
        Undismiss(key);
        Items.Add(item);
        Save();
        OnChanged();
        return OperationResult.Ok();
    }

    public OperationResult RemoveItem(DesktopItem item)
    {
        if (item is null)
        {
            return OperationResult.Fail("项目不存在。");
        }

        if (!Items.Contains(item))
        {
            return OperationResult.Fail("该项目已不在库中。");
        }

        DetachFromAllContainers(item.Id);

        if (item.Origin != ItemOrigin.Custom)
        {
            // 扫描项：记入忽略列表，防止下次扫描又被拉回
            string key = item.PathKey;
            if (!IsDismissed(key))
            {
                Library.DismissedPaths.Add(key);
            }
        }

        Items.Remove(item);
        Save();
        OnChanged();
        return OperationResult.Ok();
    }

    public bool IsDismissedPath(string fullPath)
        => !string.IsNullOrWhiteSpace(fullPath) && IsDismissed(fullPath.ToUpperInvariant());

    // ---------- 分类 ----------

    public ShortcutGroup CreateGroup(string name)
    {
        string finalName = string.IsNullOrWhiteSpace(name) ? "新建分类" : name.Trim();
        var group = new ShortcutGroup
        {
            Name = finalName,
            Order = NextGroupOrder(),
        };
        Groups.Add(group);
        SortByOrder(Groups, g => g.Order);
        EnsurePairedContainers();   // 自动建出同名分区面板
        Save();
        OnChanged();
        return group;
    }

    public void RenameGroup(ShortcutGroup group, string newName)
    {
        if (group is null || !Groups.Contains(group))
        {
            return;
        }

        string finalName = string.IsNullOrWhiteSpace(newName) ? group.Name : newName.Trim();
        if (finalName == group.Name)
        {
            return;
        }

        group.Name = finalName;
        EnsurePairedContainers();   // 同步分区标题
        Save();
        OnChanged();
    }

    public void DeleteGroup(ShortcutGroup group)
    {
        if (group is null || !Groups.Contains(group))
        {
            return;
        }

        // 只删除分类本身这条内部记录，不触碰任何桌面文件；
        // 配对的分区面板一并删除（同一份数据），成员项目仍在「全部」里
        WorkspacePanel? paired = PairedPanel(group);
        Groups.Remove(group);
        if (paired is not null)
        {
            Panels.Remove(paired);
        }
        Save();
        OnChanged();
    }

    public void MoveGroup(ShortcutGroup group, int delta)
    {
        if (group is null || delta == 0)
        {
            return;
        }

        List<ShortcutGroup> ordered = Groups.OrderBy(g => g.Order).ToList();
        int index = ordered.IndexOf(group);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        SortByOrder(Groups, g => g.Order);
        Save();
        OnChanged();
    }

    public OperationResult AssignItemToGroup(DesktopItem item, ShortcutGroup? group)
    {
        if (item is null)
        {
            return OperationResult.Fail("项目不存在。");
        }

        if (!Items.Contains(item))
        {
            return OperationResult.Fail("该项目已不在库中。");
        }

        if (group is not null && !Groups.Contains(group))
        {
            return OperationResult.Fail("分类不存在。");
        }

        DetachFromAllContainers(item.Id);
        group?.ItemIds.Add(item.Id);
        EnsurePairedContainers();   // 镜像到配对分区
        Save();
        OnChanged();
        return OperationResult.Ok();
    }

    // ---------- 自由分区 ----------

    public WorkspacePanel CreatePanel(string? title = null)
    {
        string finalTitle = string.IsNullOrWhiteSpace(title) ? "新分区" : title.Trim();

        // 层叠摆放，避免新面板与已有面板完全重叠
        int step = Panels.Count % 6;
        double offset = step * 32;
        double maxX = Math.Max(0, WorkspacePanel.CanvasWidth - WorkspacePanel.DefaultWidth);
        double maxY = Math.Max(0, WorkspacePanel.CanvasHeight - WorkspacePanel.DefaultHeight);

        var panel = new WorkspacePanel
        {
            Title = finalTitle,
            X = Math.Min(WorkspacePanel.DefaultX + offset, maxX),
            Y = Math.Min(WorkspacePanel.DefaultY + offset, maxY),
            Width = WorkspacePanel.DefaultWidth,
            Height = WorkspacePanel.DefaultHeight,
            Order = NextPanelOrder(),
        };
        Panels.Add(panel);
        SortByOrder(Panels, p => p.Order);
        EnsurePairedContainers();   // 自动建出同名分类
        Save();
        OnChanged();
        return panel;
    }

    public void RenamePanel(WorkspacePanel panel, string newTitle)
    {
        if (panel is null || !Panels.Contains(panel))
        {
            return;
        }

        string finalTitle = string.IsNullOrWhiteSpace(newTitle) ? panel.Title : newTitle.Trim();
        if (finalTitle == panel.Title)
        {
            return;
        }

        panel.Title = finalTitle;
        ShortcutGroup? pairedGroup = PairedGroup(panel);
        if (pairedGroup is not null)
        {
            pairedGroup.Name = finalTitle;
        }
        EnsurePairedContainers();
        Save();
        OnChanged();
    }

    public void DeletePanel(WorkspacePanel panel)
    {
        if (panel is null || !Panels.Contains(panel))
        {
            return;
        }

        // 只删除分区这条内部记录，不触碰任何桌面文件；
        // 配对的分类一并删除，成员项目仍在「全部」里
        ShortcutGroup? pairedGroup = PairedGroup(panel);
        Panels.Remove(panel);
        if (pairedGroup is not null)
        {
            Groups.Remove(pairedGroup);
        }
        Save();
        OnChanged();
    }

    public void MovePanel(WorkspacePanel panel, int delta)
    {
        if (panel is null || delta == 0)
        {
            return;
        }

        List<WorkspacePanel> ordered = Panels.OrderBy(p => p.Order).ToList();
        int index = ordered.IndexOf(panel);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        SortByOrder(Panels, p => p.Order);
        Save();
        OnChanged();
    }

    public void UpdatePanelLayout(WorkspacePanel panel, double x, double y, double width, double height)
    {
        if (panel is null || !Panels.Contains(panel))
        {
            return;
        }

        // 与窗口偏好同一套守卫：X/Y/Width/Height 是双向绑定到 Canvas.Left/Top/Width/Height 的，
        // 而 WPF 里 FrameworkElement.Width 的默认值就是 NaN（Auto），NaN 有可能被推回模型。
        // Math.Clamp 对 NaN 的比较全为 false，会原样放行；而 System.Text.Json 写不了
        // NaN / Infinity（直接抛 ArgumentException），异常会沿绑定 setter 一路冒上去把应用带崩。
        // 所以先做有限值校验：非有限时整个调用丢弃，保留分区当前这套有效几何。
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        double finalWidth = Math.Clamp(width, WorkspacePanel.MinWidth, WorkspacePanel.CanvasWidth);
        double finalHeight = Math.Clamp(height, WorkspacePanel.MinHeight, WorkspacePanel.CanvasHeight);
        double maxX = Math.Max(0, WorkspacePanel.CanvasWidth - finalWidth);
        double maxY = Math.Max(0, WorkspacePanel.CanvasHeight - finalHeight);
        double finalX = Math.Clamp(x, 0, maxX);
        double finalY = Math.Clamp(y, 0, maxY);

        bool changed = panel.X != finalX || panel.Y != finalY ||
                       panel.Width != finalWidth || panel.Height != finalHeight;
        if (!changed)
        {
            return;
        }

        panel.X = finalX;
        panel.Y = finalY;
        panel.Width = finalWidth;
        panel.Height = finalHeight;

        // 拖拽/缩放过程中高频调用：只持久化，不触发 Changed 避免界面重建
        Save();
    }

    public OperationResult AssignItemToPanel(DesktopItem item, WorkspacePanel? panel)
    {
        if (item is null)
        {
            return OperationResult.Fail("项目不存在。");
        }

        if (!Items.Contains(item))
        {
            return OperationResult.Fail("该项目已不在库中。");
        }

        if (panel is not null && !Panels.Contains(panel))
        {
            return OperationResult.Fail("分区不存在。");
        }

        DetachFromAllContainers(item.Id);
        if (panel is not null)
        {
            panel.ItemIds.Add(item.Id);
            // Group.ItemIds 是权威，先回写，避免下面镜像时把这次拖入覆盖掉
            ShortcutGroup? paired = PairedGroup(panel);
            paired?.ItemIds.Add(item.Id);
            AutoGrowPanel(panel);   // 阶段 20：成员变多时边框跟着长大
        }
        EnsurePairedContainers();
        Save();
        OnChanged();
        return OperationResult.Ok();
    }

    // ---------- 设置 ----------

    public void SetLayoutMode(LayoutMode mode)
    {
        if (SettingsData.LayoutMode == mode)
        {
            return;
        }

        SettingsData.LayoutMode = mode;
        // 阶段 19：切换分类导航 / 自由分区时把两边补成同一份分组定义，
        // 这样在分类导航里建的「游戏」会出现在自由分区里，成员也同步。
        EnsurePairedContainers(createMissing: true);
        Save();
        OnChanged();
    }

    public void SaveWindowPreferences(double width, double height, double? left, double? top, bool maximized)
    {
        // 窗口从未显示过时（例如一直在侧边栏/悬浮窗模式下运行），WPF 的 Left/Top 是 NaN，
        // Width/Height 也可能取到非有限值。NaN 无法写进 JSON（System.Text.Json 会直接抛
        // ArgumentException），所以这里逐项校验：尺寸非有限就保留原值，位置非有限就置空。
        if (double.IsFinite(width) && width > 0)
        {
            SettingsData.WindowWidth = width;
        }

        if (double.IsFinite(height) && height > 0)
        {
            SettingsData.WindowHeight = height;
        }

        SettingsData.WindowLeft = left is double l && double.IsFinite(l) ? l : null;
        SettingsData.WindowTop = top is double t && double.IsFinite(t) ? t : null;
        SettingsData.WindowMaximized = maximized;
        Save();
    }

    public void SaveFloatingWindowPreferences(double width, double height, double? left, double? top)
    {
        // 与主窗口同一套守卫：悬浮窗从未显示时 Left/Top 是 NaN，NaN 写不进 JSON。
        // 逐项校验，非有限值一律不落盘，避免把整个 settings.json 写坏。
        if (double.IsFinite(width) && width > 0)
        {
            SettingsData.FloatingWindowWidth = width;
        }

        if (double.IsFinite(height) && height > 0)
        {
            SettingsData.FloatingWindowHeight = height;
        }

        SettingsData.FloatingWindowLeft = left is double l && double.IsFinite(l) ? l : null;
        SettingsData.FloatingWindowTop = top is double t && double.IsFinite(t) ? t : null;
        Save();
    }

    public void Save() => SaveCore();

    // ---------- 内部实现 ----------

    private void SaveCore()
    {
        try
        {
            _libraryStore.Save();
            _configStore.Save();
        }
        catch (IOException ex)
        {
            SaveFailed?.Invoke(this, $"保存配置失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SaveFailed?.Invoke(this, $"保存配置失败（没有写入权限）：{ex.Message}");
        }
        catch (Exception ex)
        {
            // 兜底：保存路径上任何没预料到的异常都必须变成"一句人话"，不能冒到界面把应用带崩。
            // （NaN 写进 JSON 抛的就是 ArgumentException，不属于上面两类——已在上游挡住，这里再兜一层。）
            SaveFailed?.Invoke(this, $"保存配置失败：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private bool IsDismissed(string pathKey)
        => Library.DismissedPaths.Any(p => string.Equals(p, pathKey, StringComparison.OrdinalIgnoreCase));

    private void Undismiss(string pathKey)
        => Library.DismissedPaths.RemoveAll(p => string.Equals(p, pathKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>从所有分类与分区中移除指定项目（单成员制）。</summary>
    private void DetachFromAllContainers(string itemId)
    {
        foreach (ShortcutGroup group in Groups)
        {
            group.ItemIds.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
        }

        foreach (WorkspacePanel panel in Panels)
        {
            panel.ItemIds.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 阶段 19：分类导航与自由分区共用同一份分组定义。
    /// ShortcutGroup.ItemIds 是成员关系的唯一权威；WorkspacePanel 通过 GroupId 关联，
    /// 只额外保存画布几何。每次变更后双向补齐配对、把名字与成员镜像过去。
    /// </summary>
    /// <param name="createMissing">
    /// true 时给没有配对的另一边补建对象（切换布局时用）；
    /// false 只配对并同步已有对象，不改变集合数量。
    /// </param>
    private void EnsurePairedContainers(bool createMissing = false)
    {
        // 每个分类都要有一个分区面板
        foreach (ShortcutGroup group in Groups.ToList())
        {
            WorkspacePanel? panel = Panels.FirstOrDefault(p => p.GroupId == group.Id);
            if (panel is null)
            {
                // 旧数据兼容：按名字配对同名的分区
                panel = Panels.FirstOrDefault(p => p.GroupId is null && p.Title == group.Name);
                if (panel is null)
                {
                    if (!createMissing)
                    {
                        continue;
                    }

                    double offset = (Panels.Count % 6) * 32;
                    panel = new WorkspacePanel
                    {
                        Title = group.Name,
                        GroupId = group.Id,
                        X = Math.Min(WorkspacePanel.DefaultX + offset,
                            Math.Max(0, WorkspacePanel.CanvasWidth - WorkspacePanel.DefaultWidth)),
                        Y = Math.Min(WorkspacePanel.DefaultY + offset,
                            Math.Max(0, WorkspacePanel.CanvasHeight - WorkspacePanel.DefaultHeight)),
                        Width = WorkspacePanel.DefaultWidth,
                        Height = WorkspacePanel.DefaultHeight,
                        Order = NextPanelOrder(),
                    };
                    Panels.Add(panel);
                }

                panel.GroupId = group.Id;
            }

            panel.Title = group.Name;
            panel.ItemIds.Clear();
            panel.ItemIds.AddRange(group.ItemIds);
        }

        // 每个分区面板都要有一个分类（用户先在自由分区建面板的场景）
        foreach (WorkspacePanel panel in Panels.ToList())
        {
            if (panel.GroupId is not null && Groups.Any(g => g.Id == panel.GroupId))
            {
                continue;
            }

            ShortcutGroup? group = panel.GroupId is null
                ? Groups.FirstOrDefault(g => g.Name == panel.Title)
                : null;
            if (group is null)
            {
                if (!createMissing)
                {
                    continue;
                }

                group = new ShortcutGroup
                {
                    Id = string.IsNullOrEmpty(panel.GroupId) ? Guid.NewGuid().ToString("N") : panel.GroupId!,
                    Name = panel.Title,
                    Order = NextGroupOrder(),
                };
                group.ItemIds.AddRange(panel.ItemIds);
                Groups.Add(group);
                SortByOrder(Groups, g => g.Order);
            }

            panel.GroupId = group.Id;
        }

        SortByOrder(Panels, p => p.Order);
    }

    /// <summary>
    /// 阶段 20：按成员数量把分区边框放大到能装下所有磁贴，避免拖进去的软件挤在
    /// 固定的 320×240 里看不见。只放大不缩小，且不超过虚拟画布边界。
    /// </summary>
    private static void AutoGrowPanel(WorkspacePanel panel)
    {
        const double tileWidth = 120;
        const double tileHeight = 116;
        const double headerHeight = 46;
        const double padding = 20;

        int perRow = Math.Max(1, (int)((panel.Width - padding) / tileWidth));
        int rows = Math.Max(1, (panel.ItemIds.Count + perRow - 1) / perRow);
        double neededHeight = headerHeight + rows * tileHeight + padding;

        if (panel.ItemIds.Count > perRow * rows && panel.Width < 480)
        {
            panel.Width = Math.Min(480, WorkspacePanel.CanvasWidth - panel.X);
        }

        if (neededHeight > panel.Height)
        {
            double maxHeight = Math.Max(panel.Height, WorkspacePanel.CanvasHeight - panel.Y - 8);
            panel.Height = Math.Min(neededHeight, maxHeight);
        }
    }

    private WorkspacePanel? PairedPanel(ShortcutGroup group)
        => Panels.FirstOrDefault(p => p.GroupId == group.Id);

    private ShortcutGroup? PairedGroup(WorkspacePanel panel)
        => panel.GroupId is null ? null : Groups.FirstOrDefault(g => g.Id == panel.GroupId);

    private int NextGroupOrder() => Groups.Count == 0 ? 0 : Groups.Max(g => g.Order) + 1;

    private int NextPanelOrder() => Panels.Count == 0 ? 0 : Panels.Max(p => p.Order) + 1;

    private static void SortByOrder<T>(ObservableCollection<T> collection, Func<T, int> orderSelector)
    {
        List<T> sorted = collection.OrderBy(orderSelector).ToList();
        for (int target = 0; target < sorted.Count; target++)
        {
            int current = collection.IndexOf(sorted[target]);
            if (current >= 0 && current != target)
            {
                collection.Move(current, target);
            }
        }
    }
}
