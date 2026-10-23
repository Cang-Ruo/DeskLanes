using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>
/// 扫描用户桌面与公共桌面。只读取，不修改桌面上的任何文件。
/// </summary>
public sealed class DesktopScanner : IDesktopScanner
{
    private readonly IShortcutResolver _shortcutResolver;

    public DesktopScanner(IShortcutResolver shortcutResolver)
    {
        _shortcutResolver = shortcutResolver ?? throw new ArgumentNullException(nameof(shortcutResolver));
    }

    public Task<ScanResult> ScanAsync(
        ScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ScanCore(options, progress, cancellationToken), cancellationToken);

    private ScanResult ScanCore(ScanOptions? options, IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var items = new List<DesktopItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = new List<string>();

        List<(string Path, ItemOrigin Origin)> directories = ResolveDirectories(options, warnings);

        foreach ((string path, ItemOrigin origin) in directories)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // 只有成功枚举的目录才纳入"扫描范围"：目录临时不可访问时，
            // 该目录下的项目保持原有可用状态，不会被误判为失效
            if (ScanDirectory(path, origin, items, seenPaths, warnings, progress, ct))
            {
                scope.Add(path);
            }
        }

        if (ct.IsCancellationRequested)
        {
            return new ScanResult { Items = Array.Empty<DesktopItem>(), Warnings = warnings };
        }

        List<DesktopItem> ordered = items
            .OrderBy(i => i.Kind == DesktopItemKind.Folder ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult
        {
            Items = ordered,
            Warnings = warnings,
            ScopeDirectories = scope,
            UserDesktopCount = ordered.Count(i => i.Origin == ItemOrigin.UserDesktop),
            PublicDesktopCount = ordered.Count(i => i.Origin == ItemOrigin.PublicDesktop),
            ScannedAt = DateTime.Now,
        };
    }

    /// <summary>确定本次要扫描哪些目录：显式 Targets 优先，否则按 Known Folder API。</summary>
    private static List<(string Path, ItemOrigin Origin)> ResolveDirectories(
        ScanOptions? options,
        List<string> warnings)
    {
        var directories = new List<(string, ItemOrigin)>();

        if (options?.Targets is { Count: > 0 } targets)
        {
            foreach (ScanTarget target in targets)
            {
                if (string.IsNullOrWhiteSpace(target.Path))
                {
                    warnings.Add("忽略了一个空的扫描目标。");
                    continue;
                }

                directories.Add((target.Path, target.Origin));
            }

            return directories;
        }

        string? userPath = options?.UserDesktopPath ?? KnownFolders.TryGetPath(KnownFolders.FOLDERID_Desktop);
        string? publicPath = options?.PublicDesktopPath ?? KnownFolders.TryGetPublicDesktopPath();

        if (string.IsNullOrEmpty(userPath))
        {
            warnings.Add("无法通过 Known Folder API 获取用户桌面路径，已跳过用户桌面。");
        }
        else
        {
            directories.Add((userPath, ItemOrigin.UserDesktop));
        }

        if (string.IsNullOrEmpty(publicPath))
        {
            warnings.Add("无法通过 Known Folder API 获取公共桌面路径，已跳过公共桌面。");
        }
        else
        {
            directories.Add((publicPath, ItemOrigin.PublicDesktop));
        }

        return directories;
    }

    /// <summary>扫描单个目录。返回 true 表示目录成功枚举（可纳入"扫描范围"）。</summary>
    private bool ScanDirectory(
        string path,
        ItemOrigin origin,
        List<DesktopItem> items,
        HashSet<string> seenPaths,
        List<string> warnings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"正在扫描{OriginText(origin)}：{path}");

        List<FileSystemInfo> entries;
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                warnings.Add($"{OriginText(origin)}目录不存在：{path}");
                return false;
            }

            entries = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add($"没有权限访问{OriginText(origin)}：{path}");
            return false;
        }
        catch (IOException ex)
        {
            warnings.Add($"读取{OriginText(origin)}时出错：{path}（{ex.Message}）");
            return false;
        }

        foreach (FileSystemInfo entry in entries)
        {
            if (ct.IsCancellationRequested)
            {
                // 被取消：目录只枚举了一部分，不能作为判定失效的依据
                return false;
            }

            try
            {
                // desktop.ini 是系统文件，不展示
                if (entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 同一路径只出现一次（去重）
                if (!seenPaths.Add(entry.FullName))
                {
                    continue;
                }

                items.Add(CreateItem(entry, origin));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add($"跳过无法读取的项目：{entry.Name}（{ex.GetType().Name}）");
            }
        }

        return true;
    }

    private DesktopItem CreateItem(FileSystemInfo entry, ItemOrigin origin)
        => DesktopItemFactory.Create(entry.FullName, origin, _shortcutResolver);

    private static string OriginText(ItemOrigin origin)
        => origin == ItemOrigin.UserDesktop ? "用户桌面" : "公共桌面";
}
