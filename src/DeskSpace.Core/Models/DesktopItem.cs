using DeskSpace.Core.Infrastructure;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DeskSpace.Core.Models;

/// <summary>桌面项目类型。</summary>
public enum DesktopItemKind
{
    /// <summary>快捷方式（.lnk）或网址快捷方式（.url）。</summary>
    Shortcut,

    /// <summary>普通文件。</summary>
    File,

    /// <summary>文件夹。</summary>
    Folder,
}

/// <summary>项目来源（用户桌面 / 公共桌面 / 用户自定义添加）。</summary>
public enum ItemOrigin
{
    UserDesktop,

    PublicDesktop,

    /// <summary>用户通过文件选择器手动添加的外部项目。</summary>
    Custom,
}

/// <summary>
/// 桌面项目数据模型。只读取桌面元数据，不移动、不修改原始文件。
/// <para>
/// 字段分两类：扫描事实信息（FullPath/Kind/ShortcutTarget 等，随扫描刷新）与
/// 用户自定义信息（Id/AddedAt 及分类、分区中的成员关系，跨扫描保持稳定）。
/// </para>
/// </summary>
public sealed class DesktopItem : ObservableObject
{
    private bool _isAvailable = true;

    /// <summary>稳定标识（GUID）。同一路径重复扫描时复用旧 Id，保证成员关系不丢。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名称（与资源管理器一致：文件/快捷方式不含扩展名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>原始全路径。</summary>
    public string FullPath { get; set; } = string.Empty;

    public DesktopItemKind Kind { get; set; }

    public ItemOrigin Origin { get; set; }

    /// <summary>快捷方式解析出的目标（.lnk 目标路径或 .url 的 URL）；解析失败时为 null。</summary>
    public string? ShortcutTarget { get; set; }

    /// <summary>快捷方式目标是否存在：true=存在；false=已失效；null=无法确定（如 UWP 快捷方式）。</summary>
    public bool? ShortcutTargetExists { get; set; }

    /// <summary>快捷方式解析/读取过程中的错误说明（供 UI 提示与排查）。</summary>
    public string? ShortcutError { get; set; }

    /// <summary>项目当前是否可用（文件/文件夹是否仍存在于磁盘）。扫描合并时刷新。</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set => SetProperty(ref _isAvailable, value);
    }

    /// <summary>首次加入库的时间。</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次在扫描中见到的时间。</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.Now;

    /// <summary>系统图标（由 <see cref="IIconService"/> 异步填充；可能为 null，不影响名称展示）。不持久化。</summary>
    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    private ImageSource? _icon;

    /// <summary>匹配键：全路径大写形式。跨扫描识别"同一个项目"，不随显示名变化。</summary>
    [JsonIgnore]
    public string PathKey => FullPath.ToUpperInvariant();

    /// <summary>快捷方式目标已确定失效。</summary>
    [JsonIgnore]
    public bool IsBroken => Kind == DesktopItemKind.Shortcut && ShortcutTargetExists == false;

    /// <summary>磁贴上的状态徽标：空字符串表示正常。</summary>
    [JsonIgnore]
    public string StatusText => !IsAvailable ? "已失效" : IsBroken ? "目标失效" : string.Empty;

    [JsonIgnore]
    public string KindText => Kind switch
    {
        DesktopItemKind.Shortcut => "快捷方式",
        DesktopItemKind.File => "文件",
        DesktopItemKind.Folder => "文件夹",
        _ => string.Empty,
    };

    [JsonIgnore]
    public string OriginText => Origin switch
    {
        ItemOrigin.UserDesktop => "用户桌面",
        ItemOrigin.PublicDesktop => "公共桌面",
        ItemOrigin.Custom => "自定义",
        _ => string.Empty,
    };

    /// <summary>名称过长被截断时，悬停显示的完整信息。</summary>
    [JsonIgnore]
    public string TooltipText => !IsAvailable
        ? $"{Name}\n{FullPath}\n（路径已不存在）"
        : IsBroken
            ? $"{Name}\n{FullPath}\n（目标已失效：{ShortcutTarget ?? "未知"}）"
            : $"{Name}\n{FullPath}";

    /// <summary>扫描见到该项目时调用，刷新可用性与时间戳。</summary>
    public void MarkSeen(DateTime now)
    {
        IsAvailable = true;
        LastSeenAt = now;
    }
}
