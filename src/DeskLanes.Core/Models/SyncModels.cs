namespace DeskLanes.Core.Models;

/// <summary>诊断日志级别。</summary>
public enum SyncLogLevel
{
    Info,

    Warning,

    Error,
}

/// <summary>一条诊断日志。</summary>
public sealed record SyncLogEntry(DateTime Timestamp, SyncLogLevel Level, string Message);

/// <summary>需要监听的目录（用户桌面 / 公共桌面）。</summary>
public sealed record WatchTarget(string Path, ItemOrigin Origin, string DisplayName)
{
    public override string ToString() => $"{DisplayName}：{Path}";
}

/// <summary>
/// 一次可靠的路径迁移（由文件系统监听的"重命名"事件给出旧路径与新路径）。
/// 只有在操作系统明确告知"同一个项目"时才迁移，绝不靠名称或时间猜测。
/// </summary>
public sealed record PathMigration(string OldPath, string NewPath);

/// <summary>自动同步的行为参数（默认值面向"稳定优先、不过度设计"）。</summary>
public sealed class SyncOptions
{
    /// <summary>防抖窗口：事件停止后等待多久才真正扫描（默认 400 毫秒）。</summary>
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>事件持续不断时的最长延迟：超过后不再等待，立即扫描（默认 3 秒）。</summary>
    public TimeSpan MaxDebounceDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>是否启用周期性全量校正扫描（FileSystemWatcher 遗漏事件时的兜底）。</summary>
    public bool EnablePeriodicRescan { get; init; } = true;

    /// <summary>周期性校正扫描间隔（默认 5 分钟）。</summary>
    public TimeSpan PeriodicRescanInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>单个监听器的内部缓冲区大小（默认 64 KB，降低高频事件溢出概率）。</summary>
    public int WatcherBufferSize { get; init; } = 64 * 1024;

    /// <summary>内存中保留的最近日志条数（供界面提示与排查）。</summary>
    public int LogRetention { get; init; } = 60;

    /// <summary>监听恢复的最大重试次数（达到后只在周期性扫描中继续尝试）。</summary>
    public int MaxRestartAttempts { get; init; } = 20;

    /// <summary>
    /// 撞上一次正在进行的同步时，最多排队重试多少轮。
    /// 排队而不是丢弃：否则这次事件对应的变更会被遗漏（只能等周期性重扫）。
    /// 达到上限后才放弃排队，交由周期性重扫兜底。
    /// </summary>
    public int MaxDeferredPasses { get; init; } = 12;

    public static SyncOptions Default => new();
}

/// <summary>一次自动同步的结果摘要。</summary>
public sealed record SyncSummary(
    int Added,
    int UnavailableMarked,
    int Renamed,
    bool Skipped,
    string? SkipReason,
    IReadOnlyList<string> Directories,
    TimeSpan Duration);
