using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>
/// 通过 SHGetFileInfo 获取系统图标，转为可绑定的 ImageSource（已冻结，可跨线程赋值）。
/// 图标获取失败时返回 null，不影响项目名称展示。
/// </summary>
/// <remarks>
/// 缓存是有界的，两条防线缺一不可：
/// 1. <b>容量上限 + LRU 淘汰</b>：即便调用方从不清理，缓存也不会无限增长
///    （旧实现是无上限 Dictionary，文件反复增删时只增不减，属于真实的缓慢泄漏）。
/// 2. <b>按当前工作区裁剪</b>（<see cref="PruneExcept"/>）：每轮扫描/同步结束后由调用方传入
///    "现在还在的路径"，把已经不存在的项目图标从缓存里摘掉，这是主要清理手段。
/// 两条防线都用同一把锁，<see cref="GetIcon"/> 可以从后台线程调用。
/// </remarks>
public sealed class IconService : IIconService
{
    /// <summary>
    /// 默认缓存上限。32×32 图标约 4 KB/颗，加上 BitmapSource 的对象开销，
    /// 512 颗约在几 MB 量级，足够覆盖任何现实桌面（用户桌面 50 项 + 公共桌面 26 项）。
    /// </summary>
    public const int DefaultMaxCachedIcons = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxCachedIcons;

    /// <summary>单调递增的访问序号，用作 LRU 的"最近使用"标尺。</summary>
    private long _tick;

    public IconService()
        : this(DefaultMaxCachedIcons)
    {
    }

    /// <param name="maxCachedIcons">缓存上限；小于 16 时按 16 处理，避免退化成每轮都重新取图标。</param>
    public IconService(int maxCachedIcons)
    {
        _maxCachedIcons = Math.Max(16, maxCachedIcons);
    }

    /// <summary>当前生效的缓存上限。</summary>
    public int MaxCachedIcons => _maxCachedIcons;

    /// <summary>当前缓存条目数（含"取图标失败"的负结果——负结果也要缓存，否则每轮都会重试 P/Invoke）。</summary>
    public int CachedIconCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    public ImageSource? GetIcon(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(fullPath, out CacheEntry? hit))
            {
                hit.LastUsed = ++_tick;
                return hit.Source;
            }
        }

        // 取图标是 P/Invoke + 可能的 shell 慢路径，放在锁外做，避免阻塞其它线程的命中查询
        ImageSource? icon = TryExtractIcon(fullPath);

        lock (_gate)
        {
            _cache[fullPath] = new CacheEntry(icon, ++_tick);
            EvictIfNeeded();
        }

        return icon;
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    /// <summary>诊断/测试用：判断某个路径当前是否在缓存里（不改变 LRU 顺序）。</summary>
    public bool IsCached(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        lock (_gate)
        {
            return _cache.ContainsKey(fullPath);
        }
    }

    /// <summary>
    /// 只保留 <paramref name="keepPaths"/> 里的路径，其余条目全部摘掉。
    /// 每轮扫描/同步结束后调用一次，已删除或已改名文件的图标就不会长期占着内存。
    /// </summary>
    /// <returns>被摘掉的条目数。</returns>
    public int PruneExcept(IEnumerable<string> keepPaths)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepPaths is not null)
        {
            foreach (string path in keepPaths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    keep.Add(path);
                }
            }
        }

        lock (_gate)
        {
            if (_cache.Count == 0)
            {
                return 0;
            }

            List<string>? drop = null;
            foreach (string key in _cache.Keys)
            {
                if (!keep.Contains(key))
                {
                    drop ??= new List<string>();
                    drop.Add(key);
                }
            }

            if (drop is null)
            {
                return 0;
            }

            foreach (string key in drop)
            {
                _cache.Remove(key);
            }

            return drop.Count;
        }
    }

    /// <summary>
    /// 超过上限时按 LRU 淘汰一批（而不是只删一个）：一次腾出 1/8 的空间，
    /// 把 O(n) 的全表排序摊薄到每 1/8 上限次插入才做一次。
    /// </summary>
    private void EvictIfNeeded()
    {
        if (_cache.Count <= _maxCachedIcons)
        {
            return;
        }

        int target = Math.Max(1, _maxCachedIcons - (_maxCachedIcons / 8));
        int removeCount = _cache.Count - target;
        if (removeCount <= 0)
        {
            return;
        }

        List<string> oldest = _cache
            .OrderBy(pair => pair.Value.LastUsed)
            .Take(removeCount)
            .Select(pair => pair.Key)
            .ToList();

        foreach (string key in oldest)
        {
            _cache.Remove(key);
        }
    }

    private static ImageSource? TryExtractIcon(string fullPath)
    {
        var info = new ShellIcons.SHFILEINFO();
        try
        {
            IntPtr result = ShellIcons.SHGetFileInfo(
                fullPath, 0, ref info, (uint)Marshal.SizeOf<ShellIcons.SHFILEINFO>(),
                ShellIcons.SHGFI_ICON | ShellIcons.SHGFI_LARGEICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                ShellIcons.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(ImageSource? source, long lastUsed)
        {
            Source = source;
            LastUsed = lastUsed;
        }

        public ImageSource? Source { get; }

        public long LastUsed { get; set; }
    }
}
