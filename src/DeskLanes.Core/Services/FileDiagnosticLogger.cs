using System.IO;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>丢弃所有日志（测试或无日志需求时使用）。</summary>
public sealed class NullDiagnosticLogger : IDiagnosticLogger
{
    public static NullDiagnosticLogger Instance { get; } = new();

    public void Log(SyncLogLevel level, string message)
    {
        // 故意为空
    }
}

/// <summary>
/// 把同步诊断日志追加写入目录下的 sync.log，超过体积上限时轮转归档。
/// 日志写入失败绝不影响同步本身。
/// </summary>
public sealed class FileDiagnosticLogger : IDiagnosticLogger
{
    private readonly LogFileWriter _writer;

    public FileDiagnosticLogger(
        string directory,
        string fileName = "sync.log",
        int maxFileBytes = LogFileWriter.DefaultMaxFileBytes,
        int maxArchives = LogFileWriter.DefaultMaxArchives)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("日志目录不能为空。", nameof(directory));
        }

        // 目录由 LogFileWriter 负责创建（它内部已吞掉失败）
        _writer = new LogFileWriter(Path.Combine(directory, fileName), maxFileBytes, maxArchives);
    }

    public string FilePath => _writer.FilePath;

    /// <summary>底层写入器（测试与诊断用）。</summary>
    public LogFileWriter Writer => _writer;

    public void Log(SyncLogLevel level, string message)
    {
        _writer.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelText(level)}] {message}");
    }

    private static string LevelText(SyncLogLevel level) => level switch
    {
        SyncLogLevel.Warning => "警告",
        SyncLogLevel.Error => "错误",
        _ => "信息",
    };
}
