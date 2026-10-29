using System.IO;

namespace DeskLanes.Core.Services;

/// <summary>
/// 追加写日志，超过体积上限就轮转：当前文件改名为 <c>xxx.log.1</c>，
/// 旧档依次往后挪（<c>.1 → .2 → .3</c>），超过保留份数的最老一份删掉。
/// </summary>
/// <remarks>
/// 为什么用轮转而不是"原地保留后半段"：排障时最有价值的往往是**出事之前**的那一段，
/// 原地截断会把它砍掉，而轮转把它完整留在 .1 里。同时日志总量仍然有上界
/// （当前份 + 保留份数），不会像只追加不整理那样无限增长。
///
/// 任何 IO 失败一律吞掉：日志写不进去绝不能影响应用本身。
/// </remarks>
public sealed class LogFileWriter
{
    /// <summary>单份日志的体积上限（默认 512 KB）。</summary>
    public const int DefaultMaxFileBytes = 512 * 1024;

    /// <summary>除当前份之外保留的归档份数（默认 3）。</summary>
    public const int DefaultMaxArchives = 3;

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly int _maxFileBytes;
    private readonly int _maxArchives;

    /// <param name="filePath">日志文件完整路径。</param>
    /// <param name="maxFileBytes">单份体积上限；小于 16 KB 时按 16 KB 处理。</param>
    /// <param name="maxArchives">归档份数；范围 0~9，0 表示超限直接丢弃旧内容而不留档。</param>
    public LogFileWriter(string filePath, int maxFileBytes = DefaultMaxFileBytes, int maxArchives = DefaultMaxArchives)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("日志文件路径不能为空。", nameof(filePath));
        }

        _filePath = filePath;
        _maxFileBytes = Math.Max(16 * 1024, maxFileBytes);
        _maxArchives = Math.Clamp(maxArchives, 0, 9);

        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // 目录建不了时后续写入也会失败，统一吞掉
        }
    }

    /// <summary>当前正在写的日志文件。</summary>
    public string FilePath => _filePath;

    public int MaxFileBytes => _maxFileBytes;

    public int MaxArchives => _maxArchives;

    /// <summary>追加一行（自动补换行）。写失败不抛异常。</summary>
    public void Append(string line)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
                RotateIfNeeded();
            }
        }
        catch
        {
            // 日志写入失败不能影响应用运行
        }
    }

    /// <summary>当前份超过上限就轮转一次，返回是否真的轮转了。</summary>
    public bool RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length <= _maxFileBytes)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            // 保留 0 份：不留档，直接丢弃当前内容（日志仍然有上界）。
            // 不能往下走——ArchivePath(0) 会回落成 .1，那就等于偷偷留了一份。
            if (_maxArchives == 0)
            {
                File.Delete(_filePath);
                return true;
            }

            // 先删最老的一份，再依次把旧的往后挪，最后把当前文件归档。
            // 顺序不能反：否则先挪后删会多占一份的磁盘。
            string oldest = ArchivePath(_maxArchives);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = _maxArchives - 1; i >= 1; i--)
            {
                string from = ArchivePath(i);
                if (File.Exists(from))
                {
                    File.Move(from, ArchivePath(i + 1), overwrite: true);
                }
            }

            File.Move(_filePath, ArchivePath(1), overwrite: true);
            return true;
        }
        catch
        {
            // 轮转失败就让它继续长，总好过因为整理日志崩掉
            return false;
        }
    }

    /// <summary>第 n 份归档的路径（<c>xxx.log.1</c>、<c>xxx.log.2</c>…）。</summary>
    public string ArchivePath(int n)
    {
        if (n <= 1)
        {
            return _filePath + ".1";
        }

        return _filePath + "." + n;
    }
}
