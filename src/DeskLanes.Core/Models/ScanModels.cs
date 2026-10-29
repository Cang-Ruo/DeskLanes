namespace DeskLanes.Core.Models;

/// <summary>快捷方式解析结果。</summary>
public sealed class ShortcutInfo
{
    /// <summary>目标路径（.lnk）或 URL（.url）。无法通过路径解析时为 null。</summary>
    public string? Target { get; init; }

    public string Arguments { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>快捷方式自定义图标位置（为空表示使用目标默认图标）。</summary>
    public string IconLocation { get; init; } = string.Empty;

    public int IconIndex { get; init; }

    /// <summary>启动后的窗口显示方式（1 正常 / 3 最大化 / 7 最小化）。</summary>
    public int ShowCmd { get; init; }

    /// <summary>目标文件/文件夹是否存在。</summary>
    public bool TargetExists { get; init; }

    /// <summary>解析过程中的错误说明；正常解析且目标存在时为 null。</summary>
    public string? Error { get; init; }
}

/// <summary>显式指定的扫描目录（自动同步按受影响目录局部重扫时使用）。</summary>
public sealed class ScanTarget
{
    public string Path { get; init; } = string.Empty;

    public ItemOrigin Origin { get; init; }
}

/// <summary>扫描选项。路径为 null 时按 Known Folder API 自动获取（测试可注入自定义路径）。</summary>
public sealed class ScanOptions
{
    public string? UserDesktopPath { get; init; }

    public string? PublicDesktopPath { get; init; }

    /// <summary>
    /// 显式扫描目录列表。指定时只扫描这些目录（用于"只重扫受影响的目录"）；
    /// 为 null 或空时沿用 UserDesktopPath / PublicDesktopPath（或 Known Folder API）。
    /// </summary>
    public IReadOnlyList<ScanTarget>? Targets { get; init; }
}

/// <summary>一次扫描的结果。</summary>
public sealed class ScanResult
{
    public IReadOnlyList<DesktopItem> Items { get; init; } = Array.Empty<DesktopItem>();

    /// <summary>扫描过程中的警告（目录不可访问、个别项目读取失败等）；为空表示一切正常。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 本次扫描实际成功枚举的目录。合并时只有落在这些目录里的项目才会被判定失效——
    /// 目录临时不可访问时不会误伤该目录下的全部数据。
    /// </summary>
    public IReadOnlyList<string> ScopeDirectories { get; init; } = Array.Empty<string>();

    public int UserDesktopCount { get; init; }

    public int PublicDesktopCount { get; init; }

    public DateTime ScannedAt { get; init; } = DateTime.Now;
}

/// <summary>一次扫描合并的结果（供诊断日志与自动同步摘要使用）。</summary>
public sealed record ScanMergeOutcome(
    int Added,
    int Updated,
    int UnavailableMarked,
    bool Skipped,
    string? SkipReason)
{
    public static ScanMergeOutcome Skip(string reason) => new(0, 0, 0, true, reason);
}
