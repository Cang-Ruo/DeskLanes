using System.IO;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

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
/// 把同步诊断日志追加写入目录下的 sync.log。日志写入失败绝不影响同步本身。
/// </summary>
public sealed class FileDiagnosticLogger : IDiagnosticLogger
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly int _maxFileBytes;

    public FileDiagnosticLogger(string directory, string fileName = "sync.log", int maxFileBytes = 512 * 1024)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("日志目录不能为空。", nameof(directory));
        }

        _filePath = Path.Combine(directory, fileName);
        _maxFileBytes = maxFileBytes;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 目录创建失败时后续写入也会失败，统一吞掉
        }
    }

    public string FilePath => _filePath;

    public void Log(SyncLogLevel level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelText(level)}] {message}";

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
                TrimIfNeeded();
            }
        }
        catch
        {
            // 日志写入失败不能影响应用运行
        }
    }

    /// <summary>超过大小上限时保留后半部分，避免日志无限增长。</summary>
    private void TrimIfNeeded()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length <= _maxFileBytes)
            {
                return;
            }

            string[] lines = File.ReadAllLines(_filePath);
            int keep = Math.Max(50, lines.Length / 2);
            File.WriteAllLines(_filePath, lines.Skip(lines.Length - keep));
        }
        catch
        {
            // 整理失败不影响主流程
        }
    }

    private static string LevelText(SyncLogLevel level) => level switch
    {
        SyncLogLevel.Warning => "警告",
        SyncLogLevel.Error => "错误",
        _ => "信息",
    };
}
