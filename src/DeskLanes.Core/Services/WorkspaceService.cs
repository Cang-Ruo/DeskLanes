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

        // 扫描中消失的旧项目：保留在库中并标记失效，不清除用户配置与成员关系
        int marked = MarkUnavailable(existingByKey.Values, scan.ScopeDirectories);

        SettingsData.LastScanAt = now;
        Save();
        OnChanged();
        return Task.FromResult(new ScanMergeOutcome(added, updated, marked, false, null));
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
    private static int MarkUnavailable(IEnumerable<DesktopItem> leftovers, IReadOnlyList<string> scopeDirectories)
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
        foreach (DesktopItem item in leftovers)
        {
            bool inScope = scope.Count == 0
                // 未声明扫描范围（兼容旧调用/全量扫描）：桌面来源项按"扫描未见即失效"
                ? item.Origin != ItemOrigin.Custom
                : IsDirectoryInScope(item.FullPath, scope);

            if (inScope)
            {
                if (item.IsAvailable)
                {
                    item.IsAvailable = false;
                    marked++;
                }

                continue;
            }

            // 不在本次扫描范围内的桌面来源项：保持原状
            if (item.Origin != ItemOrigin.Custom)
            {
                continue;
            }

            // 自定义项：按磁盘实际存在性校正（文件被删则失效，重新出现则恢复）
            bool exists = ExistsOnDisk(item.FullPath);
            if (item.IsAvailable != exists)
            {
                item.IsAvailable = exists;
                if (!exists)
                {
                    marked++;
                }
            }
        }

        return marked;
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
        // 成员项目回到未分组池
        Groups.Remove(group);
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
        Save();
        OnChanged();
    }

    public void DeletePanel(WorkspacePanel panel)
    {
        if (panel is null || !Panels.Contains(panel))
        {
            return;
        }

        // 只删除分区这条内部记录，不触碰任何桌面文件；成员项目回到未分组池
        Panels.Remove(panel);
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
        panel?.ItemIds.Add(item.Id);
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
