using System.Collections.ObjectModel;

namespace DeskSpace.Core.Models;

/// <summary>全屏布局模式。两种模式共用同一份项目、分类与配置数据。</summary>
public enum LayoutMode
{
    /// <summary>分类导航式：左侧分类导航，右侧项目网格。</summary>
    CategoryNav,

    /// <summary>自由分区式：可拖拽、可缩放的自由面板。</summary>
    FreePanels,
}

/// <summary>
/// 窗口模式。三种模式共用同一份项目、分类、分区与配置数据，
/// 切换模式不会创建新的数据副本，也不会丢失任何用户数据。
/// </summary>
public enum WindowMode
{
    /// <summary>悬浮窗：轻量快速启动窗口（全局快捷键呼出）。</summary>
    Floating,

    /// <summary>全屏工作空间：分类导航式 / 自由分区式。</summary>
    Full,

    /// <summary>侧边栏：停靠在屏幕左侧或右侧的快捷入口栏。</summary>
    Sidebar,
}

/// <summary>侧边栏停靠位置。</summary>
public enum SidebarDock
{
    Left,

    Right,
}

/// <summary>
/// 全局快捷键定义（Win32 MOD_* 修饰键与 Virtual-Key 键值）。
/// 只使用组合键：至少需要一个修饰键，不注册单独的 Win 键，不使用键盘钩子。
/// </summary>
public sealed record HotkeyDefinition(int Modifiers, int Key)
{
    // Win32 MOD_* 常量
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>注册时始终附加：避免长按重复触发。</summary>
    public const int ModNoRepeat = 0x4000;

    public const int KeySpace = 0x20;

    /// <summary>默认快捷键：Ctrl+Alt+Space。</summary>
    public static HotkeyDefinition Default => new(ModControl | ModAlt, KeySpace);

    /// <summary>是否包含至少一个修饰键（裸键不允许，避免与系统按键冲突）。</summary>
    public bool HasModifier
        => (Modifiers & (ModAlt | ModControl | ModShift | ModWin)) != 0;

    /// <summary>是否为合法的可注册组合键。</summary>
    public bool IsValid => HasModifier && Key is > 0 and <= 0xFE;

    /// <summary>可读的组合键文本，例如 "Ctrl+Alt+Space"。</summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & ModControl) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((Modifiers & ModAlt) != 0)
            {
                parts.Add("Alt");
            }

            if ((Modifiers & ModShift) != 0)
            {
                parts.Add("Shift");
            }

            if ((Modifiers & ModWin) != 0)
            {
                parts.Add("Win");
            }

            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }
    }

    /// <summary>把 "Ctrl+Alt+Space" 形式的文本解析为快捷键；失败返回 null。</summary>
    public static HotkeyDefinition? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int modifiers = 0;
        int key = 0;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    int? parsed = ParseKey(part);
                    if (parsed is null)
                    {
                        return null;
                    }

                    key = parsed.Value;
                    break;
            }
        }

        var definition = new HotkeyDefinition(modifiers, key);
        return definition.IsValid ? definition : null;
    }

    private static int? ParseKey(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "space": return KeySpace;
            case "enter":
            case "return": return 0x0D;
            case "tab": return 0x09;
            case "esc":
            case "escape": return 0x1B;
            case "insert": return 0x2D;
            case "delete":
            case "del": return 0x2E;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": return 0x21;
            case "pagedown": return 0x22;
            case "f1": return 0x70;
            case "f2": return 0x71;
            case "f3": return 0x72;
            case "f4": return 0x73;
            case "f5": return 0x74;
            case "f6": return 0x75;
            case "f7": return 0x76;
            case "f8": return 0x77;
            case "f9": return 0x78;
            case "f10": return 0x79;
            case "f11": return 0x7A;
            case "f12": return 0x7B;
            case "f13": return 0x7C;
            case "f14": return 0x7D;
            case "f15": return 0x7E;
            case "f16": return 0x7F;
            case "f17": return 0x80;
            case "f18": return 0x81;
            case "f19": return 0x82;
            case "f20": return 0x83;
            case "f21": return 0x84;
            case "f22": return 0x85;
            case "f23": return 0x86;
            case "f24": return 0x87;
        }

        if (name.Length == 1)
        {
            char upper = char.ToUpperInvariant(name[0]);
            if (upper is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return upper;
            }
        }

        return null;
    }

    private static string KeyName(int key) => key switch
    {
        KeySpace => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        _ => ((char)key).ToString(),
    };
}

/// <summary>
/// 用户自定义分类（虚拟分类：只记录成员关系，不移动桌面上的任何文件）。
/// </summary>
public sealed class ShortcutGroup
{
    /// <summary>稳定标识（GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>分类显示名称。</summary>
    public string Name { get; set; } = "新建分类";

    /// <summary>排序序号（越小越靠前）。</summary>
    public int Order { get; set; }

    /// <summary>成员项目 Id 列表（成员关系保存在容器侧，项目侧无反向引用）。</summary>
    public List<string> ItemIds { get; set; } = new();
}

/// <summary>
/// 自由分区面板。坐标与尺寸使用虚拟画布单位（默认 1600×900），
/// 渲染时按窗口大小等比缩放，保证不同窗口尺寸下布局一致。
/// </summary>
public sealed class WorkspacePanel
{
    /// <summary>默认虚拟画布宽度。</summary>
    public const double CanvasWidth = 1600;

    /// <summary>默认虚拟画布高度。</summary>
    public const double CanvasHeight = 900;

    /// <summary>新建分区的默认宽度（虚拟单位）。</summary>
    public const double DefaultWidth = 320;

    /// <summary>新建分区的默认高度（虚拟单位）。</summary>
    public const double DefaultHeight = 240;

    /// <summary>新建分区的默认 X（虚拟单位）。</summary>
    public const double DefaultX = 48;

    /// <summary>新建分区的默认 Y（虚拟单位）。</summary>
    public const double DefaultY = 48;

    /// <summary>分区最小宽度（虚拟单位）。</summary>
    public const double MinWidth = 120;

    /// <summary>分区最小高度（虚拟单位）。</summary>
    public const double MinHeight = 80;

    /// <summary>稳定标识（GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>分区标题。</summary>
    public string Title { get; set; } = "新分区";

    /// <summary>左上角 X（虚拟单位）。</summary>
    public double X { get; set; }

    /// <summary>左上角 Y（虚拟单位）。</summary>
    public double Y { get; set; }

    /// <summary>宽度（虚拟单位）。</summary>
    public double Width { get; set; } = DefaultWidth;

    /// <summary>高度（虚拟单位）。</summary>
    public double Height { get; set; } = DefaultHeight;

    /// <summary>排序序号（越小越靠前）。</summary>
    public int Order { get; set; }

    /// <summary>成员项目 Id 列表。</summary>
    public List<string> ItemIds { get; set; } = new();
}

/// <summary>数据格式版本号。</summary>
public static class DataVersions
{
    /// <summary>当前数据版本。</summary>
    public const int Current = 1;
}

/// <summary>应用设置（窗口偏好、布局模式等）。</summary>
public sealed class AppSettings : IVersionedData
{
    public int Version { get; set; } = DataVersions.Current;

    /// <summary>当前布局模式（分类导航式 / 自由分区式）。</summary>
    public LayoutMode LayoutMode { get; set; } = LayoutMode.CategoryNav;

    public double WindowWidth { get; set; } = 1100;

    public double WindowHeight { get; set; } = 720;

    /// <summary>窗口左上角 X；null 表示由系统定位。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>窗口左上角 Y；null 表示由系统定位。</summary>
    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>最近一次扫描完成时间（启动时可用于展示）。</summary>
    public DateTime LastScanAt { get; set; }

    /// <summary>当前窗口模式（悬浮窗 / 全屏 / 侧边栏）。三种模式共用同一份数据。</summary>
    public WindowMode WindowMode { get; set; } = WindowMode.Full;

    // ---------- 全局快捷键（Win32 MOD_* / Virtual-Key） ----------

    /// <summary>快捷键修饰键（MOD_CONTROL/MOD_ALT/MOD_SHIFT/MOD_WIN 组合）。</summary>
    public int HotkeyModifiers { get; set; } = HotkeyDefinition.Default.Modifiers;

    /// <summary>快捷键主键（Virtual-Key 码，默认 VK_SPACE）。</summary>
    public int HotkeyKey { get; set; } = HotkeyDefinition.Default.Key;

    // ---------- 悬浮窗 ----------

    public double FloatingWindowWidth { get; set; } = 780;

    public double FloatingWindowHeight { get; set; } = 540;

    public double? FloatingWindowLeft { get; set; }

    public double? FloatingWindowTop { get; set; }

    /// <summary>悬浮窗失去焦点时是否自动隐藏（可关闭，避免误操作丢失窗口）。</summary>
    public bool FloatingHideOnDeactivate { get; set; } = true;

    // ---------- 侧边栏 ----------

    /// <summary>停靠位置（屏幕左侧 / 右侧）。</summary>
    public SidebarDock SidebarDock { get; set; } = SidebarDock.Right;

    /// <summary>侧边栏是否处于展开状态。</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>侧边栏宽度（展开时）。</summary>
    public double SidebarWidth { get; set; } = 280;

    /// <summary>失去焦点时是否自动收起（可关闭，避免操作过程中意外消失）。</summary>
    public bool SidebarAutoCollapse { get; set; } = true;

    // ---------- 托盘与窗口行为 ----------

    /// <summary>关闭窗口时最小化到系统托盘而不是退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 是否随 Windows 登录自动启动（写入 HKCU Run 启动项，仅当前用户、无需管理员权限）。
    /// 默认关闭：自动启动属于开机行为，必须由用户主动勾选，不在首次运行时替用户决定。
    /// </summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// 是否隐藏桌面图标（默认关闭；每次启用都会在设置里要求用户确认）。
    /// 仅记录"用户上次的选择"，实际生效由桌面图标服务写入系统设置并广播。
    /// </summary>
    public bool HideDesktopIcons { get; set; }

    /// <summary>当前快捷键定义（便于界面展示与修改）。</summary>
    public HotkeyDefinition Hotkey => new(HotkeyModifiers, HotkeyKey);
}

/// <summary>
/// 工作区库数据：项目列表 + 分类 + 自由分区 + 已忽略（用户移除）的桌面路径。
/// </summary>
public sealed class LibraryData : IVersionedData
{
    public int Version { get; set; } = DataVersions.Current;

    /// <summary>全部项目（含已失效的扫描项与自定义项）。</summary>
    public ObservableCollection<DesktopItem> Items { get; set; } = new();

    /// <summary>全部分类。</summary>
    public ObservableCollection<ShortcutGroup> Groups { get; set; } = new();

    /// <summary>全部分区面板。</summary>
    public ObservableCollection<WorkspacePanel> Panels { get; set; } = new();

    /// <summary>
    /// 用户主动移除的桌面项目全路径（大写）。这些路径在扫描合并时不会被重新拉回，
    /// 防止"移除后又出现"；用户可通过重新添加恢复。
    /// </summary>
    public List<string> DismissedPaths { get; set; } = new();
}
