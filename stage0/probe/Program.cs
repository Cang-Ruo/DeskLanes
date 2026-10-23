// ============================================================================
// DeskSpace 阶段 0 · 技术验证探针
// ----------------------------------------------------------------------------
// 目的：在本机真实验证阶段 0 列出的关键技术点是否可用。
//
// 安全承诺（本探针严格遵守）：
//   1. 只读取桌面目录内容，不创建/修改/移动/删除任何文件与快捷方式；
//   2. 不写注册表、不修改系统设置；
//   3. 不结束或重启 Explorer；
//   4. 不切换桌面图标可见性；
//   5. 全局热键仅注册数毫秒后立即注销，不拦截任何按键。
//
// 运行方式（需先安装 .NET SDK，见阶段 0 报告）：
//   dotnet run --project stage0/probe
// ============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时忽略 */ }

// ---------------------------------------------------------------- 1. Known Folder
int hrUserDesktop = Native.SHGetKnownFolderPath(Native.FOLDERID_Desktop, 0, IntPtr.Zero, out IntPtr pUserDesktop);
string? userDesktop = pUserDesktop != IntPtr.Zero ? Marshal.PtrToStringUni(pUserDesktop) : null;
if (pUserDesktop != IntPtr.Zero) Marshal.FreeCoTaskMem(pUserDesktop);

int hrPublicDesktop = Native.SHGetKnownFolderPath(Native.FOLDERID_PublicDesktop, 0, IntPtr.Zero, out IntPtr pPublicDesktop);
string? publicDesktop = pPublicDesktop != IntPtr.Zero ? Marshal.PtrToStringUni(pPublicDesktop) : null;
if (pPublicDesktop != IntPtr.Zero) Marshal.FreeCoTaskMem(pPublicDesktop);

var commonDesktop = new StringBuilder(260);
int hrCommonDesktop = Native.SHGetFolderPath(IntPtr.Zero, 0x0019 /* CSIDL_COMMON_DESKTOPDIRECTORY */, IntPtr.Zero, 0, commonDesktop);

Console.WriteLine("== 1. Known Folder API 获取桌面路径 ==");
Console.WriteLine($"  SHGetKnownFolderPath(FOLDERID_Desktop)         hr=0x{hrUserDesktop:X8}  -> {userDesktop}");
Console.WriteLine($"  SHGetKnownFolderPath(FOLDERID_PublicDesktop)   hr=0x{hrPublicDesktop:X8}  -> {publicDesktop}");
Console.WriteLine($"  SHGetFolderPath(CSIDL_COMMON_DESKTOPDIRECTORY) hr=0x{hrCommonDesktop:X8} -> {commonDesktop}");
Console.WriteLine($"  [对照] SpecialFolder.DesktopDirectory(物理用户桌面) = {Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}");
Console.WriteLine($"  [对照] SpecialFolder.Desktop(逻辑桌面)            = {Environment.GetFolderPath(Environment.SpecialFolder.Desktop)}");

// ---------------------------------------------------------------- 2. 桌面扫描（只读）
Console.WriteLine();
Console.WriteLine("== 2. 桌面内容扫描（只读枚举，不打开任何文件） ==");
foreach (var (label, path) in new[] { ("用户桌面", userDesktop), ("公共桌面", publicDesktop) })
{
    if (string.IsNullOrEmpty(path)) { Console.WriteLine($"  [{label}] 路径不可用，跳过"); continue; }
    try
    {
        int files = Directory.EnumerateFiles(path).Count();
        int dirs = Directory.EnumerateDirectories(path).Count();
        int lnks = Directory.EnumerateFiles(path, "*.lnk").Count();
        int urls = Directory.EnumerateFiles(path, "*.url").Count();
        Console.WriteLine($"  [{label}] {path}");
        Console.WriteLine($"      条目总数={files + dirs}  文件={files}  文件夹={dirs}  .lnk={lnks}  .url={urls}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{label}] 枚举失败: {ex.GetType().Name}: {ex.Message}");
    }
}

// ---------------------------------------------------------------- 3. IShellLinkW
Console.WriteLine();
Console.WriteLine("== 3. IShellLinkW 解析快捷方式（取用户桌面第一个 .lnk 做样本） ==");
string? firstLnk = null;
if (!string.IsNullOrEmpty(userDesktop))
{
    try { firstLnk = Directory.EnumerateFiles(userDesktop, "*.lnk").FirstOrDefault(); }
    catch (Exception ex) { Console.WriteLine($"  枚举 .lnk 失败: {ex.Message}"); }
}

if (firstLnk is null)
{
    Console.WriteLine("  用户桌面上没有 .lnk 文件，跳过解析");
}
else
{
    try
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(firstLnk, 0);
        var target = new StringBuilder(260);
        var findData = new WIN32_FIND_DATA();
        link.GetPath(target, target.Capacity, ref findData, 0x4 /* SLGP_RAWPATH */);
        var arguments = new StringBuilder(260);
        link.GetArguments(arguments, arguments.Capacity);
        var workDir = new StringBuilder(260);
        link.GetWorkingDirectory(workDir, workDir.Capacity);
        var iconPath = new StringBuilder(260);
        link.GetIconLocation(iconPath, iconPath.Capacity, out int iconIndex);
        link.GetShowCmd(out int showCmd);
        string t = target.ToString();
        Console.WriteLine($"  样本文件   : {firstLnk}");
        Console.WriteLine($"  目标路径   : {(t.Length > 0 ? t : "(空 - 可能为 UWP/商店应用，需改用 IDList 解析)")}");
        Console.WriteLine($"  启动参数   : {arguments}");
        Console.WriteLine($"  工作目录   : {workDir}");
        Console.WriteLine($"  图标位置   : {(iconPath.Length > 0 ? $"{iconPath} #{iconIndex}" : "(未指定，使用目标默认图标)")}");
        Console.WriteLine($"  显示方式   : {showCmd}");
        if (t.Length > 0)
            Console.WriteLine($"  目标存在   : {File.Exists(t) || Directory.Exists(t)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  解析失败: {ex.GetType().Name}: {ex.Message}");
    }
}

// ---------------------------------------------------------------- 4. 图标获取
Console.WriteLine();
Console.WriteLine("== 4. 图标获取（SHGetFileInfo / IShellItemImageFactory） ==");
foreach (var p in new[] { firstLnk, userDesktop })
{
    if (string.IsNullOrEmpty(p)) continue;
    try
    {
        var fi = new SHFILEINFO();
        IntPtr r = Native.SHGetFileInfo(p, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(),
            Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
        bool ok = r != IntPtr.Zero && fi.hIcon != IntPtr.Zero;
        Console.WriteLine($"  SHGetFileInfo({Path.GetFileName(p)}): {(ok ? $"成功 hIcon=0x{fi.hIcon.ToInt64():X}" : "失败")}  类型名={fi.szTypeName}");
        if (ok) Native.DestroyIcon(fi.hIcon);
    }
    catch (Exception ex) { Console.WriteLine($"  SHGetFileInfo 异常: {ex.Message}"); }
}

if (!string.IsNullOrEmpty(userDesktop))
{
    try
    {
        Native.SHCreateItemFromParsingName(userDesktop, IntPtr.Zero, Native.IID_IShellItemImageFactory, out IShellItemImageFactory factory);
        factory.GetImage(new SIZE { cx = 256, cy = 256 }, SIIGBF.IconOnly, out IntPtr hbm);
        bool ok = hbm != IntPtr.Zero;
        Console.WriteLine($"  IShellItemImageFactory(256x256): {(ok ? $"成功 hbm=0x{hbm.ToInt64():X}" : "失败")}");
        if (ok) Native.DeleteObject(hbm);
    }
    catch (Exception ex) { Console.WriteLine($"  IShellItemImageFactory 失败: {ex.GetType().Name}: {ex.Message}"); }
}

// ---------------------------------------------------------------- 5. 全局热键
Console.WriteLine();
Console.WriteLine("== 5. RegisterHotKey 全局热键行为验证（每次注册后立即注销） ==");
IntPtr msgWnd = Native.CreateMessageWindow();
if (msgWnd == IntPtr.Zero)
{
    Console.WriteLine("  消息窗口创建失败，跳过热键测试");
}
else
{
    void TryHotkey(int id, uint modifiers, uint vk, string description)
    {
        bool registered = Native.RegisterHotKey(msgWnd, id, modifiers, vk);
        int error = Marshal.GetLastWin32Error();
        string result = registered
            ? "注册成功（已立即注销）"
            : $"注册失败 err={error}{(error == 1409 ? "（ERROR_HOTKEY_ALREADY_REGISTERED，已被其他程序占用）" : "")}";
        Console.WriteLine($"  {description,-26} -> {result}");
        if (registered) Native.UnregisterHotKey(msgWnd, id);
    }

    const uint MOD_ALT = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_SHIFT = 0x0004;
    const uint MOD_WIN = 0x0008;
    const uint VK_LWIN = 0x5B;
    const uint VK_F12 = 0x7B;
    const uint VK_Z = 0x5A;
    const uint VK_SPACE = 0x20;

    TryHotkey(2, MOD_WIN, VK_LWIN, "Win 键单独触发");
    TryHotkey(3, MOD_WIN | MOD_SHIFT, VK_Z, "Win+Shift+Z");
    TryHotkey(5, MOD_CONTROL | MOD_ALT | MOD_SHIFT, VK_SPACE, "Ctrl+Alt+Shift+Space");

    // 真正的冲突检测：先保持注册一个热键，再用同一组合注册第二个 ID
    bool kept = Native.RegisterHotKey(msgWnd, 1, MOD_CONTROL | MOD_ALT, VK_F12);
    Console.WriteLine($"  {"Ctrl+Alt+F12（保持注册）",-26} -> {(kept ? "注册成功" : "注册失败")}");
    TryHotkey(4, MOD_CONTROL | MOD_ALT, VK_F12, "Ctrl+Alt+F12（冲突注册）");
    if (kept)
    {
        Native.UnregisterHotKey(msgWnd, 1);
        Console.WriteLine("  （冲突测试所用的热键已注销）");
    }

    Native.DestroyWindow(msgWnd);
}

// ---------------------------------------------------------------- 6. FileSystemWatcher
Console.WriteLine();
Console.WriteLine("== 6. FileSystemWatcher 启动验证（不制造任何文件变化） ==");
if (!string.IsNullOrEmpty(userDesktop))
{
    try
    {
        using var watcher = new FileSystemWatcher(userDesktop)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        Console.WriteLine($"  监听目录 : {watcher.Path}");
        Console.WriteLine($"  已启用   : {watcher.EnableRaisingEvents}  缓冲区: {watcher.InternalBufferSize} 字节  包含子目录: {watcher.IncludeSubdirectories}");
        Console.WriteLine("  说明     : 事件响应与压力表现需在阶段 1 用临时目录专项测试");
    }
    catch (Exception ex) { Console.WriteLine($"  启动失败: {ex.GetType().Name}: {ex.Message}"); }
}

Console.WriteLine();
Console.WriteLine("== 探针执行完毕 ==");

// ============================================================================
// Win32 / Shell 互操作声明
// ============================================================================
static class Native
{
    // Known Folder ID（KnownFolders.h）
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid FOLDERID_PublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetFolderPath(IntPtr hwnd, int csidl, IntPtr hToken, uint dwFlags, StringBuilder pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    public const uint SHGFI_ICON = 0x00000100;
    public const uint SHGFI_LARGEICON = 0x00000000;
    public const uint SHGFI_SMALLICON = 0x00000001;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    // ---- 消息窗口 + 全局热键 ----
    private static WndProc? _wndProc;

    public static IntPtr CreateMessageWindow()
    {
        _wndProc = DefaultWndProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "DeskSpaceStage0ProbeMsgWindow"
        };
        if (RegisterClass(ref wc) == 0) return IntPtr.Zero;
        return CreateWindowEx(0, wc.lpszClassName, string.Empty, 0,
            0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static IntPtr DefaultWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hWnd, msg, wParam, lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }
}

// ---- IShellLinkW / IPersistFile / ShellLink ----
[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW
{
    void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, ref WIN32_FIND_DATA pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.I4)] int dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
class ShellLink { }

// ---- IShellItemImageFactory（高分辨率图标/缩略图） ----
[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemImageFactory
{
    void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential)]
struct SIZE { public int cx; public int cy; }

enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10 }

// ---- 结构体 ----
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WIN32_FIND_DATA
{
    public uint dwFileAttributes;
    public FILETIME ftCreationTime;
    public FILETIME ftLastAccessTime;
    public FILETIME ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
}

[StructLayout(LayoutKind.Sequential)]
struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
}
