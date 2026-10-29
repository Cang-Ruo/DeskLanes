using System.Collections.ObjectModel;
using System.Windows.Media;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>扫描用户桌面与公共桌面，产出项目列表。</summary>
public interface IDesktopScanner
{
    Task<ScanResult> ScanAsync(
        ScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>解析 .lnk 快捷方式的真实目标、参数、工作目录等信息。</summary>
public interface IShortcutResolver
{
    ShortcutInfo? Resolve(string linkPath);
}

/// <summary>获取项目的系统图标。</summary>
public interface IIconService
{
    ImageSource? GetIcon(string fullPath);

    void ClearCache();
}

/// <summary>用 Windows Shell 机制启动项目。</summary>
public interface ILaunchService
{
    LaunchResult Launch(DesktopItem item);
}

/// <summary>
/// 桌面自动同步：监听用户桌面与公共桌面的变化，防抖后在后台线程重新扫描并合并。
/// 监听不可用时降级为周期性重新扫描，保证数据最终一致。
/// </summary>
public interface IDesktopSyncService : IDisposable
{
    /// <summary>同步服务是否已启动。</summary>
    bool IsRunning { get; }

    /// <summary>是否至少有一个目录正在被监听（false 表示处于降级轮询状态）。</summary>
    bool IsWatchActive { get; }

    /// <summary>是否处于降级状态（没有任何目录被监听，仅靠周期性重扫）。</summary>
    bool IsDegraded { get; }

    /// <summary>供界面展示的同步状态文字。</summary>
    string StatusText { get; }

    IReadOnlyList<string> WatchedDirectories { get; }

    /// <summary>监听失败或目录不可访问的目录。</summary>
    IReadOnlyList<string> FailedDirectories { get; }

    /// <summary>最近的诊断日志（新的在前）。</summary>
    IReadOnlyList<SyncLogEntry> RecentLog { get; }

    /// <summary>一次同步完成（含跳过）后触发。</summary>
    event EventHandler<SyncSummary>? SyncCompleted;

    /// <summary>监听状态变化（启动/降级/恢复）后触发。</summary>
    event EventHandler? StatusChanged;

    /// <summary>启动监听。应在 UI 线程调用，以便把结果合并回 UI 线程。</summary>
    void Start();

    /// <summary>停止监听（保留周期性扫描定时器，直到 Dispose）。</summary>
    void Stop();

    /// <summary>立即执行一次全量重新扫描并合并（刷新按钮 / 恢复运行后校正）。</summary>
    Task<SyncSummary> SyncNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>诊断日志输出（自动同步用）。</summary>
public interface IDiagnosticLogger
{
    void Log(SyncLogLevel level, string message);
}

/// <summary>用户提示与输入（错误/信息/确认/文本输入/文件与文件夹选择）。由 WPF 层实现，便于在无界面环境下测试。</summary>
public interface IDialogService
{
    void ShowError(string message, string title = "DeskLanes");

    void ShowInfo(string message, string title = "DeskLanes");

    /// <summary>是/否确认。返回 true 表示用户选择"是"。</summary>
    bool Confirm(string message, string title = "DeskLanes");

    /// <summary>单行文本输入。取消时返回 null。</summary>
    string? Prompt(string message, string title = "DeskLanes", string initialValue = "");

    /// <summary>选择文件。取消时返回 null。</summary>
    string? PickFile(string title = "选择项目", string filter = "所有文件|*.*");

    /// <summary>选择文件夹。取消时返回 null。</summary>
    string? PickFolder(string title = "选择文件夹");
}

/// <summary>
/// 工作区服务：合并桌面扫描、维护项目库、分类与自由分区、应用设置。
/// 所有变更安全持久化到 %LocalAppData%\DeskLanes\。
/// </summary>
public interface IWorkspaceService
{
    /// <summary>全部项目（含失效项与自定义项）。UI 可直接绑定。</summary>
    ObservableCollection<DesktopItem> Items { get; }

    /// <summary>全部分类（按 Order 升序）。</summary>
    ObservableCollection<ShortcutGroup> Groups { get; }

    /// <summary>全部分区面板（按 Order 升序）。</summary>
    ObservableCollection<WorkspacePanel> Panels { get; }

    /// <summary>应用设置。</summary>
    AppSettings Settings { get; }

    /// <summary>加载配置时产生的警告（损坏/迁移/版本过新等）。</summary>
    IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>结构性变更事件（分类/分区/成员关系/项目增删）。UI 订阅后刷新展示。</summary>
    event EventHandler? Changed;

    /// <summary>保存失败（磁盘错误/权限问题）时触发，参数为中文说明。</summary>
    event EventHandler<string>? SaveFailed;

    /// <summary>
    /// 把一次扫描结果合并进库：复用稳定 Id、刷新事实信息、消失项标记失效。
    /// 返回合并结果（新增/刷新/失效数量），跳过合并时给出原因（如目录不可访问）。
    /// </summary>
    Task<ScanMergeOutcome> MergeScanAsync(ScanResult scan, CancellationToken cancellationToken = default);

    /// <summary>
    /// 应用可靠的路径迁移（重命名 / 同目录内移动）：操作系统明确给出"旧路径→新路径"时，
    /// 复用同一个项目 Id 与成员关系，只更新事实信息。无法确定归属时不做任何猜测。
    /// 返回实际迁移的项目数。
    /// </summary>
    int ApplyPathMigrations(IReadOnlyList<PathMigration> migrations);

    /// <summary>用户自定义添加文件/快捷方式/文件夹。路径必须存在。</summary>
    OperationResult AddCustomItem(string fullPath);

    /// <summary>某路径是否被用户移除过（在忽略列表中，扫描不会拉回）。</summary>
    bool IsDismissedPath(string fullPath);

    /// <summary>从库中移除项目（扫描项记入忽略列表防止重扫回归；自定义项直接删除）。</summary>
    OperationResult RemoveItem(DesktopItem item);

    ShortcutGroup CreateGroup(string name);

    void RenameGroup(ShortcutGroup group, string newName);

    void DeleteGroup(ShortcutGroup group);

    /// <summary>按排序移动分类（delta 为 -1 上移 / +1 下移）。</summary>
    void MoveGroup(ShortcutGroup group, int delta);

    /// <summary>设置项目所属分类（单成员制：先从所有分类和分区移除，再加入目标分类；group 为 null 表示回到未分组）。</summary>
    OperationResult AssignItemToGroup(DesktopItem item, ShortcutGroup? group);

    WorkspacePanel CreatePanel(string? title = null);

    void RenamePanel(WorkspacePanel panel, string newTitle);

    void DeletePanel(WorkspacePanel panel);

    void MovePanel(WorkspacePanel panel, int delta);

    /// <summary>更新分区几何（拖拽/缩放过程调用）。只保存，不触发 Changed 事件，避免拖拽中界面重建。</summary>
    void UpdatePanelLayout(WorkspacePanel panel, double x, double y, double width, double height);

    /// <summary>设置项目所属分区（单成员制）。panel 为 null 表示回到未分组。</summary>
    OperationResult AssignItemToPanel(DesktopItem item, WorkspacePanel? panel);

    /// <summary>切换布局模式（分类导航式 / 自由分区式），两种模式数据共用。</summary>
    void SetLayoutMode(LayoutMode mode);

    /// <summary>保存窗口偏好（关闭/尺寸变化时调用）。</summary>
    void SaveWindowPreferences(double width, double height, double? left, double? top, bool maximized);

    /// <summary>立即持久化。</summary>
    void Save();
}
