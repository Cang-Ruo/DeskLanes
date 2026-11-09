using System.Diagnostics;
using System.IO;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>
/// 桌面自动同步服务。
/// <para>
/// 工作方式：用 FileSystemWatcher 监听用户桌面与公共桌面 → 事件只做登记（后台线程）
/// → 防抖窗口结束后才真正扫描并合并 → 合并结果回到 UI 线程应用。
/// 监听失败/目录不可访问时降级为周期性重新扫描，保证数据最终一致。
/// </para>
/// <para>
/// 不做的事：不移动/删除/修改桌面上的任何文件；不靠文件名或时间猜测项目身份
/// （只有操作系统明确给出"旧路径→新路径"的重命名事件才迁移关联关系）。
/// </para>
/// </summary>
public sealed class DesktopSyncService : IDesktopSyncService
{
    private readonly IWorkspaceService _workspace;
    private readonly IDesktopScanner _scanner;
    private readonly List<WatchTarget> _targets;
    private readonly SyncOptions _options;
    private readonly IDiagnosticLogger _logger;

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<FileSystemWatcher, WatchTarget> _targetByWatcher = new();
    private readonly Dictionary<string, FileSystemWatcher> _watcherByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _restartAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyncLogEntry> _recentLog = new();
    private readonly HashSet<string> _pendingDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PathMigration> _pendingMigrations = new();

    private System.Threading.Timer? _debounceTimer;
    private System.Threading.Timer? _periodicTimer;
    private SynchronizationContext? _uiContext;
    private DateTime _pendingSince;
    private int _syncInFlight;
    private int _deferredPasses;
    private bool _running;
    private bool _disposed;
    private bool _isDegraded;
    private string _statusText = "自动同步：未启动";

    public DesktopSyncService(
        IWorkspaceService workspace,
        IDesktopScanner scanner,
        IEnumerable<WatchTarget> targets,
        SyncOptions? options = null,
        IDiagnosticLogger? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        if (targets is null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        _options = options ?? SyncOptions.Default;
        _logger = logger ?? NullDiagnosticLogger.Instance;
        _targets = targets
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Path))
            .DistinctBy(t => t.Path.TrimEnd('\\', '/').ToUpperInvariant())
            .ToList();
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public bool IsWatchActive
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count > 0;
            }
        }
    }

    public bool IsDegraded
    {
        get
        {
            lock (_gate)
            {
                return _isDegraded;
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                return _statusText;
            }
        }
    }

    public IReadOnlyList<string> WatchedDirectories
    {
        get
        {
            lock (_gate)
            {
                return _watchers
                    .Select(w => w.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            }
        }
    }

    public IReadOnlyList<string> FailedDirectories
    {
        get
        {
            lock (_gate)
            {
                return _failedDirectories.ToList();
            }
        }
    }

    public IReadOnlyList<SyncLogEntry> RecentLog
    {
        get
        {
            lock (_gate)
            {
                return _recentLog.ToList();
            }
        }
    }

    public event EventHandler<SyncSummary>? SyncCompleted;

    public event EventHandler? StatusChanged;

    // ---------- 生命周期 ----------

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DesktopSyncService));
        }

        lock (_gate)
        {
            if (_running)
            {
                // 防止重复订阅与重复创建监听器
                return;
            }

            _running = true;
            // UI 线程调用时这里拿到 Dispatcher 上下文，合并结果就会回到 UI 线程；
            // 无界面环境（控制台测试）为 null，直接在当前线程执行
            _uiContext = SynchronizationContext.Current;
        }

        _debounceTimer = new System.Threading.Timer(
            _ => _ = OnDebounceElapsedAsync(), null, Timeout.Infinite, Timeout.Infinite);

        int started = 0;
        foreach (WatchTarget target in _targets)
        {
            if (TryCreateWatcher(target, quiet: false))
            {
                started++;
            }
        }

        if (_options.EnablePeriodicRescan)
        {
            _periodicTimer = new System.Threading.Timer(
                _ => _ = PeriodicTickAsync(),
                null,
                _options.PeriodicRescanInterval,
                _options.PeriodicRescanInterval);
        }

        UpdateStatus();

        Log(SyncLogLevel.Info,
            $"自动同步已启动：监听 {started}/{_targets.Count} 个目录" +
            (_options.EnablePeriodicRescan
                ? $"，兜底重扫间隔 {_options.PeriodicRescanInterval.TotalMinutes:0} 分钟"
                : "（未启用周期性重扫）"));

        if (started == 0 && _targets.Count > 0)
        {
            Log(SyncLogLevel.Warning, "没有任何目录可以监听，已降级为周期性重新扫描。");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _periodicTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        List<FileSystemWatcher> snapshot;
        lock (_gate)
        {
            snapshot = _watchers.ToList();
            _pendingDirectories.Clear();
            _pendingMigrations.Clear();
        }

        foreach (FileSystemWatcher watcher in snapshot)
        {
            RemoveWatcher(watcher);
        }

        UpdateStatus();
        Log(SyncLogLevel.Info, "自动同步已停止。");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _debounceTimer?.Dispose();
        _periodicTimer?.Dispose();
        _debounceTimer = null;
        _periodicTimer = null;
    }

    public Task<SyncSummary> SyncNowAsync(CancellationToken cancellationToken = default)
        => RunSyncAsync(AllDirectories(), Array.Empty<PathMigration>(), "手动触发", cancellationToken);

    // ---------- 事件登记与防抖 ----------

    private void OnCreated(object sender, FileSystemEventArgs e) => Register(sender, e);

    private void OnDeleted(object sender, FileSystemEventArgs e) => Register(sender, e);

    private void OnChanged(object sender, FileSystemEventArgs e) => Register(sender, e);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsIgnoredName(e.FullPath) && IsIgnoredName(e.OldFullPath))
        {
            return;
        }

        // 操作系统明确告知的"旧路径 → 新路径"：可靠，可用于迁移关联关系
        Register(sender, e, new PathMigration(e.OldFullPath, e.FullPath));
    }

    private void Register(object sender, FileSystemEventArgs e, PathMigration? migration = null)
    {
        if (IsIgnoredName(e.FullPath))
        {
            return;
        }

        string? directory = ResolveDirectory(sender, e.FullPath);
        if (directory is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_running)
            {
                return;
            }

            bool wasEmpty = _pendingDirectories.Count == 0;
            _pendingDirectories.Add(directory);
            if (migration is not null)
            {
                _pendingMigrations.Add(migration);
            }

            if (wasEmpty)
            {
                _pendingSince = DateTime.Now;
            }

            // 事件持续不断时不再无限等待：超过最长延迟立即扫描
            bool overdue = DateTime.Now - _pendingSince >= _options.MaxDebounceDelay;
            int due = overdue ? 0 : (int)Math.Max(1, _options.DebounceInterval.TotalMilliseconds);
            _debounceTimer?.Change(due, Timeout.Infinite);
        }
    }

    private async Task OnDebounceElapsedAsync()
    {
        List<string> directories;
        List<PathMigration> migrations;

        lock (_gate)
        {
            if (_disposed || !_running || _pendingDirectories.Count == 0)
            {
                return;
            }

            directories = _pendingDirectories.ToList();
            migrations = _pendingMigrations.ToList();
            _pendingDirectories.Clear();
            _pendingMigrations.Clear();
        }

        await RunSyncAsync(directories, migrations, "文件变化").ConfigureAwait(false);
    }

    private async Task PeriodicTickAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_running)
            {
                return;
            }
        }

        TryRestoreFailedWatchers();
        await RunSyncAsync(AllDirectories(), Array.Empty<PathMigration>(), "定时校正").ConfigureAwait(false);
    }

    // ---------- 同步执行 ----------

    private async Task<SyncSummary> RunSyncAsync(
        IReadOnlyList<string> directories,
        IReadOnlyList<PathMigration> migrations,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
        {
            // 已有同步在进行：把待处理项放回队列，等这一轮结束后再跑一轮。
            // 绝不能直接丢弃——否则本次事件对应的变更会被遗漏，只能等周期性重扫。
            lock (_gate)
            {
                foreach (string pending in directories)
                {
                    _pendingDirectories.Add(pending);
                }

                foreach (PathMigration migration in migrations)
                {
                    _pendingMigrations.Add(migration);
                }

                _deferredPasses++;
                if (_deferredPasses > _options.MaxDeferredPasses)
                {
                    _pendingDirectories.Clear();
                    _pendingMigrations.Clear();
                    _deferredPasses = 0;
                    Log(SyncLogLevel.Warning,
                        $"同步请求过于密集（连续 {_options.MaxDeferredPasses} 轮排队），" +
                        "已放弃排队并交由周期性重扫校正。");
                    return new SyncSummary(0, 0, 0, true, "同步请求过于密集", directories, TimeSpan.Zero);
                }
            }

            Log(SyncLogLevel.Info, $"已有同步在进行，本次{reason}排队等待下一轮（第 {_deferredPasses} 轮）。");
            int retryDelay = (int)Math.Max(50, _options.DebounceInterval.TotalMilliseconds);
            _debounceTimer?.Change(retryDelay, Timeout.Infinite);
            return new SyncSummary(0, 0, 0, true, "已有同步在进行（已排队等待下一轮）", directories, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ScanOptions options = BuildScanOptions(directories);
            ScanResult scan = await _scanner.ScanAsync(options, null, cancellationToken).ConfigureAwait(false);

            // 重命名迁移与合并都发生在 UI 线程：集合与属性变更安全
            int renamed = 0;
            ScanMergeOutcome outcome = await RunOnUiAsync(async () =>
            {
                renamed = migrations.Count > 0 ? _workspace.ApplyPathMigrations(migrations) : 0;
                return await _workspace.MergeScanAsync(scan, cancellationToken);
            }).ConfigureAwait(false);

            return Finish(outcome, renamed, directories, stopwatch, reason);
        }
        catch (OperationCanceledException)
        {
            return new SyncSummary(0, 0, 0, true, "已取消", directories, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            // 单次同步失败绝不能让应用退出
            Log(SyncLogLevel.Error, $"自动同步失败（{reason}）：{ex.GetType().Name}：{ex.Message}");
            return new SyncSummary(0, 0, 0, true, ex.Message, directories, stopwatch.Elapsed);
        }
        finally
        {
            lock (_gate)
            {
                // 一轮真正跑完后重置排队计数
                _deferredPasses = 0;
            }

            Interlocked.Exchange(ref _syncInFlight, 0);
        }
    }

    private SyncSummary Finish(
        ScanMergeOutcome outcome,
        int renames,
        IReadOnlyList<string> directories,
        Stopwatch stopwatch,
        string reason)
    {
        stopwatch.Stop();
        var summary = new SyncSummary(
            outcome.Added,
            outcome.UnavailableMarked,
            renames,
            outcome.Skipped,
            outcome.SkipReason,
            directories,
            stopwatch.Elapsed);

        if (outcome.Skipped)
        {
            Log(SyncLogLevel.Warning, $"同步跳过（{reason}）：{outcome.SkipReason}");
        }
        else if (outcome.Added == 0 && outcome.UnavailableMarked == 0
                 && outcome.Removed == 0 && renames == 0)
        {
            Log(SyncLogLevel.Info,
                $"同步完成（{reason}）：无变化，扫描 {directories.Count} 个目录，耗时 {stopwatch.ElapsedMilliseconds} 毫秒");
        }
        else
        {
            Log(SyncLogLevel.Info,
                $"同步完成（{reason}）：新增 {outcome.Added}，失效 {outcome.UnavailableMarked}，" +
                $"清除已删除 {outcome.Removed}，重命名迁移 {renames}，" +
                $"耗时 {stopwatch.ElapsedMilliseconds} 毫秒");
        }

        SyncCompleted?.Invoke(this, summary);
        return summary;
    }

    private ScanOptions BuildScanOptions(IReadOnlyList<string> directories)
    {
        List<ScanTarget> targets;
        lock (_gate)
        {
            targets = _targets
                .Where(t => directories.Count == 0 || directories.Any(d => SamePath(d, t.Path)))
                .Select(t => new ScanTarget { Path = t.Path, Origin = t.Origin })
                .ToList();
        }

        return new ScanOptions { Targets = targets };
    }

    private List<string> AllDirectories()
    {
        lock (_gate)
        {
            return _targets.Select(t => t.Path).ToList();
        }
    }

    /// <summary>把合并动作送回 UI 线程（集合与属性变更安全）；无同步上下文时直接执行。</summary>
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        SynchronizationContext? context = _uiContext;
        if (context is null)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ => _ = RunAndCompleteAsync(action, completion), null);

        return completion.Task;
    }

    private static async Task RunAndCompleteAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> completion)
    {
        try
        {
            completion.SetResult(await action().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    // ---------- 监听器管理 ----------

    private bool TryCreateWatcher(WatchTarget target, bool quiet)
    {
        if (string.IsNullOrWhiteSpace(target.Path))
        {
            return false;
        }

        lock (_gate)
        {
            if (_watcherByDirectory.ContainsKey(target.Path))
            {
                return true;
            }
        }

        try
        {
            if (!Directory.Exists(target.Path))
            {
                if (!quiet)
                {
                    Log(SyncLogLevel.Warning, $"{target.DisplayName}目录不存在，暂不监听：{target.Path}");
                }

                lock (_gate)
                {
                    _failedDirectories.Add(target.Path);
                }

                return false;
            }

            var watcher = new FileSystemWatcher(target.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = _options.WatcherBufferSize,
            };

            watcher.Created += OnCreated;
            watcher.Deleted += OnDeleted;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            lock (_gate)
            {
                _watchers.Add(watcher);
                _watcherByDirectory[target.Path] = watcher;
                _targetByWatcher[watcher] = target;
                _failedDirectories.Remove(target.Path);
                _restartAttempts[target.Path] = 0;
            }

            Log(SyncLogLevel.Info, $"开始监听{target.DisplayName}：{target.Path}");
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or PlatformNotSupportedException)
        {
            if (!quiet)
            {
                Log(SyncLogLevel.Error,
                    $"无法监听{target.DisplayName}（{ex.GetType().Name}：{ex.Message}），" +
                    $"已降级为周期性重新扫描：{target.Path}");
            }

            lock (_gate)
            {
                _failedDirectories.Add(target.Path);
            }

            return false;
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var watcher = sender as FileSystemWatcher;
        string path = ResolveDirectory(sender, null) ?? "（未知目录）";
        Exception? inner = e.GetException();

        WatchTarget? target = null;
        if (watcher is not null)
        {
            lock (_gate)
            {
                _targetByWatcher.TryGetValue(watcher, out target);
            }
        }

        Log(SyncLogLevel.Warning,
            $"监听{(target?.DisplayName ?? "目录")}时出现错误（{inner?.GetType().Name}：{inner?.Message}），" +
            $"将重新扫描校正：{path}");

        // 事件可能已经丢失：立即安排一次该目录的重扫
        if (watcher is not null && !string.IsNullOrEmpty(watcher.Path))
        {
            Register(sender, new SimpleEventArgs(Path.Combine(watcher.Path, ".")));
        }

        // 监听器可能已经失效：延迟后尝试重建（不能在事件处理线程里 Dispose 自身）
        _ = Task.Delay(1500).ContinueWith(
            _ => RestartWatcher(watcher),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void RestartWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        WatchTarget? target;
        lock (_gate)
        {
            _targetByWatcher.TryGetValue(watcher, out target);
        }

        if (target is null)
        {
            return;
        }

        RemoveWatcher(watcher);
        bool restarted = TryCreateWatcher(target, quiet: false);
        if (!restarted)
        {
            lock (_gate)
            {
                int attempts = _restartAttempts.GetValueOrDefault(target.Path) + 1;
                _restartAttempts[target.Path] = attempts;
                if (attempts >= _options.MaxRestartAttempts)
                {
                    Log(SyncLogLevel.Warning,
                        $"监听{target.DisplayName}连续失败 {attempts} 次，停止重试，仅保留周期性重扫：{target.Path}");
                }
            }
        }

        UpdateStatus();
    }

    /// <summary>周期性扫描时顺带尝试恢复此前失败的监听（目录重新可访问的场景）。</summary>
    private void TryRestoreFailedWatchers()
    {
        List<WatchTarget> pending;
        lock (_gate)
        {
            pending = _targets
                .Where(t => _failedDirectories.Contains(t.Path))
                .Where(t => _restartAttempts.GetValueOrDefault(t.Path) < _options.MaxRestartAttempts)
                .ToList();
        }

        foreach (WatchTarget target in pending)
        {
            if (TryCreateWatcher(target, quiet: true))
            {
                Log(SyncLogLevel.Info, $"监听已恢复：{target.DisplayName}（{target.Path}）");
                UpdateStatus();
            }
            else
            {
                lock (_gate)
                {
                    _restartAttempts[target.Path] = _restartAttempts.GetValueOrDefault(target.Path) + 1;
                }
            }
        }
    }

    private void RemoveWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Deleted -= OnDeleted;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
        }
        catch
        {
            // 解绑失败也要继续释放
        }

        lock (_gate)
        {
            _watchers.Remove(watcher);
            _targetByWatcher.Remove(watcher);
            foreach (KeyValuePair<string, FileSystemWatcher> pair in _watcherByDirectory.ToList())
            {
                if (ReferenceEquals(pair.Value, watcher))
                {
                    _watcherByDirectory.Remove(pair.Key);
                }
            }
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
            // 释放失败无需处理
        }
    }

    private void UpdateStatus()
    {
        int total;
        int active;
        bool running;

        lock (_gate)
        {
            total = _targets.Count;
            active = _watchers.Count;
            running = _running;

            if (!running)
            {
                _isDegraded = false;
                _statusText = "自动同步：未启动";
            }
            else if (total == 0)
            {
                _isDegraded = true;
                _statusText = "自动同步：没有可监听的桌面目录";
            }
            else if (active == total)
            {
                _isDegraded = false;
                _statusText = $"自动同步：监听中（{active}/{total}）";
            }
            else if (active == 0)
            {
                _isDegraded = true;
                _statusText = _options.EnablePeriodicRescan
                    ? $"自动同步：降级轮询（每 {_options.PeriodicRescanInterval.TotalMinutes:0} 分钟重扫）"
                    : "自动同步：未监听（目录不可访问）";
            }
            else
            {
                _isDegraded = true;
                _statusText = $"自动同步：部分监听（{active}/{total}）";
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 辅助 ----------

    private string? ResolveDirectory(object? sender, string? fullPath)
    {
        if (sender is FileSystemWatcher watcher && !string.IsNullOrEmpty(watcher.Path))
        {
            return watcher.Path;
        }

        return string.IsNullOrEmpty(fullPath) ? null : Path.GetDirectoryName(fullPath);
    }

    private static bool IsIgnoredName(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return true;
        }

        string name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return name.Length == 0 || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private void Log(SyncLogLevel level, string message)
    {
        var entry = new SyncLogEntry(DateTime.Now, level, message);

        lock (_gate)
        {
            _recentLog.Insert(0, entry);
            int keep = Math.Max(1, _options.LogRetention);
            if (_recentLog.Count > keep)
            {
                _recentLog.RemoveRange(keep, _recentLog.Count - keep);
            }
        }

        _logger.Log(level, message);
    }

    /// <summary>仅需携带路径时的轻量事件参数（内部使用）。</summary>
    private sealed class SimpleEventArgs : FileSystemEventArgs
    {
        public SimpleEventArgs(string fullPath)
            : base(WatcherChangeTypes.Changed, Path.GetDirectoryName(fullPath) ?? ".", Path.GetFileName(fullPath))
        {
        }
    }
}
