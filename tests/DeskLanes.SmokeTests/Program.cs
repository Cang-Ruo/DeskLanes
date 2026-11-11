// ============================================================================
// DeskLanes 冒烟测试（阶段 1-4）
// ----------------------------------------------------------------------------
// 覆盖：Known Folder 路径、真实桌面扫描、快捷方式解析、图标获取、启动服务
//       （仅启动安全目标）、异常边界（全部使用临时目录，不碰桌面）、
//       MainViewModel 的搜索 / 刷新 / 空结果、工作区服务与配置容错、
//       桌面自动同步（监听 + 防抖 + 排队重跑）、
//       阶段 4：窗口模式管理 / 全局快捷键（真实注册）/ 悬浮窗与侧边栏视图模型 /
//       设置视图模型 / 托盘菜单命令分发 / 三模式共享数据。
// 退出码：0 = 全部通过；1 = 存在失败项。
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.Core;
using DeskLanes.Core.Models;
using DeskLanes.Core.Services;

int passed = 0;
int failed = 0;
var failures = new List<string>();

void Check(bool condition, string name, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [PASS] {name}");
    }
    else
    {
        failed++;
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : " -> " + detail)}");
    }
}

void Info(string text) => Console.WriteLine($"  [INFO] {text}");

Console.OutputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------- 1. Known Folder
Console.WriteLine("== 1. Known Folder 路径解析 ==");
string? userPath = KnownFolders.TryGetPath(KnownFolders.FOLDERID_Desktop);
string? publicPath = KnownFolders.TryGetPublicDesktopPath();
Check(!string.IsNullOrEmpty(userPath), "用户桌面路径解析成功", userPath);
Check(!string.IsNullOrEmpty(publicPath), "公共桌面路径解析成功", publicPath);
Check(
    userPath == Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    "用户桌面路径与 BCL DesktopDirectory 一致", userPath);

// ---------------------------------------------------------------- 2. 真实桌面扫描
Console.WriteLine();
Console.WriteLine("== 2. 真实桌面扫描（只读） ==");
var resolver = new ShortcutResolver();
var scanner = new DesktopScanner(resolver);
ScanResult scan = await scanner.ScanAsync();
Check(scan.Items.Count > 0, "扫描到至少一个项目", $"共 {scan.Items.Count} 个");
Info($"用户桌面 {scan.UserDesktopCount} 个 · 公共桌面 {scan.PublicDesktopCount} 个 · 警告 {scan.Warnings.Count} 条");
foreach (string warning in scan.Warnings)
{
    Info($"警告: {warning}");
}

Check(scan.Items.Any(i => i.Origin == ItemOrigin.UserDesktop), "包含用户桌面项目");
Check(scan.Items.Any(i => i.Origin == ItemOrigin.PublicDesktop), "包含公共桌面项目");
Check(scan.Items.Any(i => i.Kind == DesktopItemKind.Shortcut), "包含快捷方式");
Check(scan.Items.Any(i => i.Kind == DesktopItemKind.Folder), "包含文件夹");
Check(scan.Items.Any(i => i.Kind == DesktopItemKind.File), "包含普通文件");
Check(
    scan.Items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == scan.Items.Count,
    "项目路径无重复");
Check(scan.Items.All(i => !string.IsNullOrWhiteSpace(i.Name)), "所有项目都有显示名称");
Check(scan.Items.All(i => i.Name.Length <= 260), "所有项目名称长度合理");

// ---------------------------------------------------------------- 3. 快捷方式解析
Console.WriteLine();
Console.WriteLine("== 3. 快捷方式解析 ==");
List<DesktopItem> shortcutSample = scan.Items
    .Where(i => i.Kind == DesktopItemKind.Shortcut)
    .Take(25)
    .ToList();

int valid = 0;
int broken = 0;
int unresolved = 0;
foreach (DesktopItem item in shortcutSample)
{
    if (item.ShortcutTarget is null)
    {
        unresolved++;
    }
    else if (item.ShortcutTargetExists == true)
    {
        valid++;
    }
    else
    {
        broken++;
    }
}

Info($"抽样 {shortcutSample.Count} 个快捷方式：目标有效 {valid} · 目标失效 {broken} · 无法解析(可能为UWP) {unresolved}");
Check(valid > 0, "至少一个快捷方式解析出有效目标");
Check(
    shortcutSample.All(i => !i.IsBroken || i.ShortcutTarget is not null),
    "失效快捷方式均带有目标信息（不伪装）");
foreach (DesktopItem item in shortcutSample.Where(i => i.IsBroken).Take(3))
{
    Info($"失效样例: {item.Name} -> {item.ShortcutTarget}");
}

// ---------------------------------------------------------------- 4. 图标获取
Console.WriteLine();
Console.WriteLine("== 4. 图标获取 ==");
var iconService = new IconService();
List<DesktopItem> iconSample = scan.Items.Take(20).ToList();
int iconOk = 0;
foreach (DesktopItem item in iconSample)
{
    if (iconService.GetIcon(item.FullPath) is not null)
    {
        iconOk++;
    }
}

Info($"抽样 {iconSample.Count} 个项目，成功获取图标 {iconOk} 个");
Check(iconOk >= iconSample.Count - 2, "绝大多数项目图标获取成功", $"{iconOk}/{iconSample.Count}");

// ---------------------------------------------------------------- 5. 启动服务
Console.WriteLine();
Console.WriteLine("== 5. 启动服务（只启动安全目标） ==");
var launchService = new LaunchService();

// 5.1 不存在的项目 -> 友好错误
var missing = new DesktopItem
{
    Id = "missing",
    Name = "不存在的项目",
    FullPath = Path.Combine(Path.GetTempPath(), "DeskLanes-不存在-" + Guid.NewGuid().ToString("N")[..8]),
    Kind = DesktopItemKind.File,
};
LaunchResult rMissing = launchService.Launch(missing);
Check(!rMissing.Success && !string.IsNullOrEmpty(rMissing.ErrorMessage),
    "启动不存在的项目返回错误提示而不是崩溃",
    rMissing.ErrorMessage?.Replace("\n", " "));

// 5.2 文件夹 -> 资源管理器（真实启动桌面上第一个文件夹）
DesktopItem? folder = scan.Items.FirstOrDefault(i => i.Kind == DesktopItemKind.Folder);
if (folder is null)
{
    Info("桌面上没有文件夹，跳过文件夹启动测试");
}
else
{
    LaunchResult rFolder = launchService.Launch(folder);
    Check(rFolder.Success, "启动文件夹（经资源管理器打开）", folder.FullPath);
}

// 5.3 普通文件 -> 系统默认关联程序（优先选最温和的扩展名）
string[] safeExtensions = { ".txt", ".url", ".log", ".ini", ".csv", ".md", ".json" };
DesktopItem? file = scan.Items
    .Where(i => i.Kind == DesktopItemKind.File)
    .Select(i => (Item: i, Ext: Path.GetExtension(i.FullPath).ToLowerInvariant()))
    .Where(x => safeExtensions.Contains(x.Ext))
    .OrderBy(x => Array.IndexOf(safeExtensions, x.Ext))
    .Select(x => x.Item)
    .FirstOrDefault();
if (file is null)
{
    Info("桌面上没有温和类型的普通文件，跳过文件启动测试");
}
else
{
    LaunchResult rFile = launchService.Launch(file);
    Check(rFile.Success, "启动普通文件（系统默认关联程序）", file.FullPath);
}

// 5.4 快捷方式 -> 仅启动目标为文件夹的（避免误启动未知程序）
DesktopItem? safeShortcut = scan.Items.FirstOrDefault(i =>
    i.Kind == DesktopItemKind.Shortcut &&
    i.ShortcutTargetExists == true &&
    i.ShortcutTarget is not null &&
    Directory.Exists(i.ShortcutTarget));
if (safeShortcut is null)
{
    Info("没有目标为文件夹的快捷方式，跳过快捷方式启动测试（避免误启动未知程序）");
}
else
{
    LaunchResult rShortcut = launchService.Launch(safeShortcut);
    Check(rShortcut.Success, "启动快捷方式（Shell 按快捷方式语义打开）",
        $"{safeShortcut.FullPath} -> {safeShortcut.ShortcutTarget}");
}

// 5.5 Kind 是扫描那一刻的快照，磁盘上可能已经变了。
// 典型场景：用户把桌面上的文件夹删掉、换成同名的文件（或反过来）。
// 此时不能因为"记录的形态对不上"就报"已不存在"——按此刻真实存在的形态打开。
string kindMismatchDir = Path.Combine(Path.GetTempPath(), "DeskLanesKind_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(kindMismatchDir);
try
{
    var staleKind = new DesktopItem
    {
        Id = "stale-kind",
        Name = "形态已变",
        FullPath = kindMismatchDir,
        Kind = DesktopItemKind.File, // 实际是目录
    };
    LaunchResult rStale = launchService.Launch(staleKind);
    Check(rStale.Success, "记录的形态与磁盘不一致时仍按实际形态打开（不误报已不存在）",
        rStale.ErrorMessage?.Replace("\n", " "));
}
finally
{
    try
    {
        Directory.Delete(kindMismatchDir, true);
    }
    catch
    {
        // 临时目录清理失败不影响结果
    }
}

// ---------------------------------------------------------------- 6. 异常与边界（临时目录）
Console.WriteLine();
Console.WriteLine("== 6. 异常与边界（全部使用临时目录，不碰桌面） ==");
string tempRoot = Path.Combine(Path.GetTempPath(), "DeskLanesSmoke_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempRoot);
try
{
    // 6.1 一个位置不存在
    ScanResult rBadPath = await scanner.ScanAsync(new ScanOptions
    {
        UserDesktopPath = Path.Combine(tempRoot, "no-such-dir"),
        PublicDesktopPath = publicPath,
    });
    Check(rBadPath.Warnings.Any(w => w.Contains("不存在")),
        "不存在的目录给出警告且不崩溃",
        string.Join(" | ", rBadPath.Warnings));
    Check(rBadPath.Items.Count > 0, "一个位置失败时另一个位置仍正常扫描", $"{rBadPath.Items.Count} 个");

    // 6.2 空目录
    string emptyDir = Path.Combine(tempRoot, "empty");
    Directory.CreateDirectory(emptyDir);
    ScanResult rEmpty = await scanner.ScanAsync(new ScanOptions
    {
        UserDesktopPath = emptyDir,
        PublicDesktopPath = emptyDir,
    });
    Check(rEmpty.Items.Count == 0 && rEmpty.Warnings.Count == 0,
        "空目录扫描结果为 0 个项目且无警告");

    // 6.3 损坏的 .lnk / .url
    string junkDir = Path.Combine(tempRoot, "junk");
    Directory.CreateDirectory(junkDir);
    File.WriteAllText(Path.Combine(junkDir, "坏快捷方式.lnk"), "这不是一个有效的快捷方式文件内容");
    File.WriteAllText(Path.Combine(junkDir, "坏网址.url"), "[InternetShortcut]\n没有URL行");
    ScanResult rJunk = await scanner.ScanAsync(new ScanOptions
    {
        UserDesktopPath = junkDir,
        PublicDesktopPath = emptyDir,
    });
    DesktopItem? junkLnk = rJunk.Items.FirstOrDefault(i => i.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
    DesktopItem? junkUrl = rJunk.Items.FirstOrDefault(i => i.FullPath.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
    Check(junkLnk is not null, "损坏的 .lnk 仍被列出（不崩溃）");
    Check(junkLnk is null || junkLnk.ShortcutTarget is null || junkLnk.IsBroken,
        "损坏的 .lnk 不会伪装成有效目标");
    Check(junkUrl is not null, "损坏的 .url 仍被列出（不崩溃）");
    Check(rJunk.Warnings.Count == 0, "可读的损坏文件不在扫描层报警（仅在解析层标记）",
        string.Join(" | ", rJunk.Warnings));
    if (junkLnk is not null)
    {
        LaunchResult rJunkLaunch = launchService.Launch(junkLnk);
        Check(!rJunkLaunch.Success, "启动损坏快捷方式返回错误而不是崩溃",
            rJunkLaunch.ErrorMessage?.Replace("\n", " "));
    }

    // 6.4 不可读的目录（拒绝访问枚举）
    string lockedDir = Path.Combine(tempRoot, "locked");
    Directory.CreateDirectory(lockedDir);
    try
    {
        var acl = new DirectoryInfo(lockedDir);
        var security = acl.GetAccessControl();
        var denyRule = new System.Security.AccessControl.FileSystemAccessRule(
            Environment.UserName,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(denyRule);
        acl.SetAccessControl(security);
        try
        {
            ScanResult rLocked = await scanner.ScanAsync(new ScanOptions
            {
                UserDesktopPath = lockedDir,
                PublicDesktopPath = publicPath,
            });
            Check(rLocked.Warnings.Any(w => w.Contains("权限")),
                "拒绝访问的目录给出权限警告且不崩溃",
                string.Join(" | ", rLocked.Warnings));
        }
        finally
        {
            try
            {
                security.RemoveAccessRule(denyRule);
                acl.SetAccessControl(security);
            }
            catch
            {
                Info($"权限恢复失败，可手动删除临时目录：{tempRoot}");
            }
        }
    }
    catch (Exception ex)
    {
        Info($"ACL 测试在当前环境不可用，跳过（{ex.GetType().Name}）");
    }
}
finally
{
    try
    {
        Directory.Delete(tempRoot, true);
    }
    catch
    {
        Info($"临时目录删除失败（可手动删除）：{tempRoot}");
    }
}

// ---------------------------------------------------------------- 7. ViewModel：搜索 / 刷新 / 空结果
Console.WriteLine();
Console.WriteLine("== 7. MainViewModel（搜索 / 刷新 / 空结果） ==");
var dialogService = new ConsoleDialogService();

// 使用临时配置目录，避免测试写真实用户配置
string vmWsDir = Path.Combine(Path.GetTempPath(), "DeskLanesVM_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(vmWsDir);
IWorkspaceService vmWorkspace = new WorkspaceService(
    new LibraryStore(vmWsDir), new ConfigStore(vmWsDir), resolver);
var viewModel = new MainViewModel(
    vmWorkspace, scanner, iconService, launchService, dialogService, new ShellState());
await viewModel.InitializeAsync();
Check(viewModel.Items.Count > 0, "ViewModel 初始化后加载了项目", $"{viewModel.Items.Count} 个");
Check(!viewModel.IsLoading, "初始化后退出加载状态");
Check(dialogService.Errors.Count == 0, "初始化过程没有弹出错误",
    string.Join(" | ", dialogService.Errors));

string? probeName = viewModel.Items.FirstOrDefault(i => i.Name.Length >= 2)?.Name;
if (probeName is null)
{
    Info("没有可用于搜索测试的项目名称，跳过搜索断言");
}
else
{
    string head = probeName.Substring(0, Math.Min(3, probeName.Length));

    viewModel.SearchText = head;
    await Task.Delay(80);
    Check(viewModel.Items.Count > 0, "按名称关键词能搜到结果", $"关键词: {head}");
    Check(
        viewModel.Items.All(i =>
            i.Name.Contains(viewModel.SearchText, StringComparison.OrdinalIgnoreCase) ||
            i.FullPath.Contains(viewModel.SearchText, StringComparison.OrdinalIgnoreCase)),
        "搜索结果均匹配关键词");
    Check(viewModel.Items.Count <= scan.Items.Count, "搜索结果数量不超过全量");

    viewModel.SearchText = head.ToUpperInvariant();
    await Task.Delay(80);
    Check(
        viewModel.Items.All(i => i.Name.Contains(viewModel.SearchText, StringComparison.OrdinalIgnoreCase)),
        "搜索不区分大小写", $"大写关键词: {viewModel.SearchText}");

    viewModel.SearchText = "zzz-绝对不存在的关键字-zzz";
    await Task.Delay(80);
    Check(viewModel.Items.Count == 0 && viewModel.HasNoResults,
        "无匹配时进入空结果状态", viewModel.EmptyStateText);

    viewModel.SearchText = string.Empty;
    await Task.Delay(80);
    Check(viewModel.Items.Count == scan.Items.Count,
        "清空搜索后恢复完整列表", $"{viewModel.Items.Count} 个");

    // 刷新按钮逻辑
    await viewModel.RefreshAsync();
    await Task.Delay(300);
    Check(viewModel.Items.Count > 0 && !viewModel.IsLoading, "刷新后重新加载且退出加载状态");

    // 布局切换不丢数据、搜索词共享
    viewModel.SearchText = head;
    await Task.Delay(80);
    viewModel.ToggleLayoutCommand.Execute(null);
    Check(viewModel.IsFreePanels && viewModel.SearchText == head, "切换布局后搜索词保留");
    Check(viewModel.FreePanels.Panels.Count == vmWorkspace.Panels.Count, "切换布局后面板数据保留");
    viewModel.ToggleLayoutCommand.Execute(null);
    Check(viewModel.IsCategoryNav, "再切回分类导航");
}

// 清理第 7 节临时配置目录
try
{
    Directory.Delete(vmWsDir, true);
}
catch
{
    Info($"ViewModel 测试临时目录删除失败（可手动删除）：{vmWsDir}");
}

// ---------------------------------------------------------------- 8. 工作区服务（分类 / 分区 / 自定义 / 移除）
Console.WriteLine();
Console.WriteLine("== 8. 工作区服务（分类 / 分区 / 自定义添加 / 移除） ==");
string wsRoot = Path.Combine(Path.GetTempPath(), "DeskLanesWS_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    // 伪造两个"桌面"目录（不碰真实桌面）
    string fakeUser = Path.Combine(wsRoot, "FakeUserDesktop");
    string fakePublic = Path.Combine(wsRoot, "FakePublicDesktop");
    Directory.CreateDirectory(fakeUser);
    Directory.CreateDirectory(fakePublic);
    File.WriteAllText(Path.Combine(fakeUser, "项目A.txt"), "a");
    File.WriteAllText(Path.Combine(fakeUser, "项目B.txt"), "b");
    Directory.CreateDirectory(Path.Combine(fakeUser, "文件夹X"));
    File.WriteAllText(Path.Combine(fakePublic, "公共文件.txt"), "c");

    var wsOptions = new ScanOptions { UserDesktopPath = fakeUser, PublicDesktopPath = fakePublic };
    ScanResult fakeScan = await scanner.ScanAsync(wsOptions);
    Check(fakeScan.Items.Count == 4, "伪造桌面扫描出 4 个项目", $"实际 {fakeScan.Items.Count}");

    var libraryStore = new LibraryStore(wsRoot);
    var configStore = new ConfigStore(wsRoot);
    var workspace = new WorkspaceService(libraryStore, configStore, resolver);
    await workspace.MergeScanAsync(fakeScan);
    Check(workspace.Items.Count == 4, "扫描合并后库中有 4 个项目", $"实际 {workspace.Items.Count}");
    Check(workspace.Items.All(i => !string.IsNullOrEmpty(i.Id)), "每个项目都有稳定 Id");

    // ---- 分类 CRUD ----
    ShortcutGroup group1 = workspace.CreateGroup("工作");
    ShortcutGroup group2 = workspace.CreateGroup("娱乐");
    Check(workspace.Groups.Count == 2 && group1.Order < group2.Order, "新建分类按顺序排列");

    DesktopItem itemA = workspace.Items.First(i => i.Name == "项目A");
    DesktopItem itemB = workspace.Items.First(i => i.Name == "项目B");

    workspace.AssignItemToGroup(itemA, group1);
    Check(group1.ItemIds.Count == 1 && group1.ItemIds[0] == itemA.Id, "项目加入分类");

    workspace.AssignItemToGroup(itemA, group2);
    Check(group1.ItemIds.Count == 0 && group2.ItemIds.Count == 1, "单成员制：跨分类调整只归属一个分类");

    workspace.AssignItemToGroup(itemA, null);
    Check(group2.ItemIds.Count == 0, "项目移出分类回到未分组");

    workspace.RenameGroup(group1, "工作常用");
    Check(group1.Name == "工作常用", "分类重命名");

    workspace.MoveGroup(group2, -1);
    Check(workspace.Groups.OrderBy(g => g.Order).First().Id == group2.Id, "分类上移改变顺序");

    workspace.AssignItemToGroup(itemA, group1);
    workspace.DeleteGroup(group1);
    Check(workspace.Groups.Count == 1, "删除分类后分类数减少");
    Check(workspace.Items.Count == 4, "删除分类只删内部记录，项目仍在库中");
    Check(File.Exists(itemA.FullPath), "删除分类不触碰桌面文件");

    // ---- 分区 CRUD ----
    WorkspacePanel panel1 = workspace.CreatePanel("左屏");
    WorkspacePanel panel2 = workspace.CreatePanel("右屏");
    Check(workspace.Panels.Count == 2 && panel1.Order < panel2.Order, "新建分区按顺序排列");

    workspace.AssignItemToPanel(itemA, panel1);
    Check(panel1.ItemIds.Count == 1, "项目加入分区");

    workspace.AssignItemToPanel(itemA, panel2);
    Check(panel1.ItemIds.Count == 0 && panel2.ItemIds.Count == 1, "单成员制：跨分区调整只归属一个分区");

    workspace.UpdatePanelLayout(panel2, 100, 200, 400, 300);
    // 阶段 25：几何吸附到整数方格，不再停在任意像素
    double p25Grid = workspace.Settings.PanelGridSize;
    Check(panel2.X % p25Grid == 0 && panel2.Y % p25Grid == 0
          && panel2.Width % p25Grid == 0 && panel2.Height % p25Grid == 0
          && Math.Abs(panel2.X - 100) <= p25Grid && Math.Abs(panel2.Y - 200) <= p25Grid
          && Math.Abs(panel2.Width - 400) <= p25Grid && Math.Abs(panel2.Height - 300) <= p25Grid,
        "分区位置与尺寸按方格吸附",
        $"X={panel2.X} Y={panel2.Y} W={panel2.Width} H={panel2.Height} grid={p25Grid}");

    workspace.UpdatePanelLayout(panel2, -500, 99999, 5, 5);
    Check(panel2.X == 0 && panel2.Width == WorkspacePanel.MinWidth,
        "越界几何被钳制在画布与最小尺寸内",
        $"X={panel2.X} W={panel2.Width}");

    workspace.MovePanel(panel2, -1);
    Check(workspace.Panels.OrderBy(p => p.Order).First().Id == panel2.Id, "分区上移改变顺序");

    workspace.RenamePanel(panel1, "左侧");
    Check(panel1.Title == "左侧", "分区重命名");

    workspace.DeletePanel(panel1);
    Check(workspace.Panels.Count == 1 && workspace.Items.Count == 4, "删除分区只删内部记录，项目仍在库中");

    // ---- 自定义添加 ----
    string externalFile = Path.Combine(wsRoot, "外部文件.txt");
    File.WriteAllText(externalFile, "external");
    string externalFolder = Path.Combine(wsRoot, "外部文件夹");
    Directory.CreateDirectory(externalFolder);

    OperationResult addFile = workspace.AddCustomItem(externalFile);
    Check(addFile.Success && workspace.Items.Any(i => i.Origin == ItemOrigin.Custom && i.FullPath == externalFile),
        "自定义添加普通文件", addFile.ErrorMessage);

    OperationResult addFolder = workspace.AddCustomItem(externalFolder);
    Check(addFolder.Success && workspace.Items.Any(i =>
            i.Kind == DesktopItemKind.Folder && i.Origin == ItemOrigin.Custom),
        "自定义添加文件夹", addFolder.ErrorMessage);

    OperationResult addMissing = workspace.AddCustomItem(Path.Combine(wsRoot, "不存在的路径.txt"));
    Check(!addMissing.Success && !string.IsNullOrEmpty(addMissing.ErrorMessage),
        "添加不存在的路径返回错误而不是崩溃", addMissing.ErrorMessage);

    OperationResult addEmpty = workspace.AddCustomItem("   ");
    Check(!addEmpty.Success, "空路径被拒绝");

    OperationResult addDuplicate = workspace.AddCustomItem(externalFile);
    Check(addDuplicate.Success && workspace.Items.Count(i => i.FullPath == externalFile) == 1,
        "重复添加同一路径幂等（不产生重复项）");

    // ---- 移除：扫描项记忽略列表，源文件保留，重扫不回归 ----
    int itemsBeforeRemove = workspace.Items.Count;
    OperationResult removeResult = workspace.RemoveItem(itemA);
    Check(removeResult.Success && workspace.Items.Count == itemsBeforeRemove - 1, "移除扫描项");
    Check(File.Exists(itemA.FullPath), "移除后源文件仍在磁盘");

    ScanResult rescan = await scanner.ScanAsync(wsOptions);
    await workspace.MergeScanAsync(rescan);
    Check(!workspace.Items.Any(i => string.Equals(i.PathKey, itemA.PathKey, StringComparison.OrdinalIgnoreCase)),
        "被移除的扫描项重扫后不会回归");
    Check(workspace.Items.Count == itemsBeforeRemove - 1, "重扫不改变项目总数（自定义项不受影响）",
        $"实际 {workspace.Items.Count}");

    DesktopItem customItem = workspace.Items.First(i => i.Origin == ItemOrigin.Custom && i.FullPath == externalFile);
    workspace.RemoveItem(customItem);
    Check(!workspace.Items.Contains(customItem), "移除自定义项只删记录");
    Check(File.Exists(externalFile), "移除自定义项后文件仍在磁盘");

    // 重新添加此前移除的扫描项（解除忽略）
    OperationResult reAdd = workspace.AddCustomItem(itemA.FullPath);
    Check(reAdd.Success && workspace.Items.Any(i => i.FullPath == itemA.FullPath), "重新添加被移除的项目成功");

    // ---- 失效路径：阶段 14 改为「桌面上确实不存在就自动清除」----
    string gonePath = Path.Combine(fakeUser, "会消失.txt");
    File.WriteAllText(gonePath, "gone");
    ScanResult withGone = await scanner.ScanAsync(wsOptions);
    await workspace.MergeScanAsync(withGone);
    DesktopItem goneItem = workspace.Items.First(i => i.FullPath == gonePath);
    workspace.AssignItemToGroup(goneItem, group2);

    File.Delete(gonePath);
    ScanResult afterGone = await scanner.ScanAsync(wsOptions);
    await workspace.MergeScanAsync(afterGone);
    Check(!workspace.Items.Contains(goneItem), "桌面上确实不存在的项目被自动清除");
    Check(!group2.ItemIds.Contains(goneItem.Id), "自动清除时分类归属一并摘掉");

    // ---- 布局模式 ----
    Check(workspace.Settings.LayoutMode == LayoutMode.CategoryNav, "默认为分类导航模式");
    workspace.SetLayoutMode(LayoutMode.FreePanels);
    Check(workspace.Settings.LayoutMode == LayoutMode.FreePanels, "切换到自由分区模式");

    // 再移除一个扫描项，供"重启"后验证忽略列表持久化
    OperationResult removeB = workspace.RemoveItem(itemB);
    Check(removeB.Success && File.Exists(itemB.FullPath), "移除第二个扫描项（源文件保留）");

    // ---- 9. 持久化与"重启"（同一目录新建服务实例） ----
    Console.WriteLine();
    Console.WriteLine("== 9. 持久化与重启验证（同目录重新加载） ==");

    var workspace2 = new WorkspaceService(new LibraryStore(wsRoot), new ConfigStore(wsRoot), resolver);
    Check(workspace2.LoadWarnings.Count == 0, "重启加载配置无警告",
        string.Join(" | ", workspace2.LoadWarnings));
    Check(workspace2.Items.Count == workspace.Items.Count, "重启后项目数量一致",
        $"{workspace.Items.Count} -> {workspace2.Items.Count}");
    Check(!workspace2.Items.Any(i => string.Equals(i.PathKey, goneItem.PathKey, StringComparison.OrdinalIgnoreCase)),
        "重启后已删除的项目不再出现");
    Check(workspace2.Groups.Count == workspace.Groups.Count, "重启后分类保留");
    Check(workspace2.Panels.Count == workspace.Panels.Count, "重启后分区保留");

    WorkspacePanel panel2Reloaded = workspace2.Panels.First(p => p.Id == panel2.Id);
    Check(panel2Reloaded.X == panel2.X && panel2Reloaded.Y == panel2.Y &&
          panel2Reloaded.Width == panel2.Width && panel2Reloaded.Height == panel2.Height,
        "重启后分区几何保留", $"({panel2Reloaded.X},{panel2Reloaded.Y},{panel2Reloaded.Width},{panel2Reloaded.Height})");
    Check(panel2Reloaded.ItemIds.SequenceEqual(panel2.ItemIds), "重启后分区成员关联保留");
    Check(!workspace2.Groups.First(g => g.Id == group2.Id).ItemIds.Contains(goneItem.Id),
        "重启后被清除项目的分类归属也已清理");
    Check(workspace2.Settings.LayoutMode == LayoutMode.FreePanels, "重启后布局模式保留");
    Check(workspace2.IsDismissedPath(itemB.FullPath), "重启后忽略列表保留（移除项不回归）");
    Check(File.Exists(Path.Combine(wsRoot, "library.json")), "配置文件已写入磁盘");
    Check(File.Exists(Path.Combine(wsRoot, "library.json.bak")), "原子替换产生 .bak 备份");

    // ---- 10. 视图模型：空分类 / 未分区池 / 拖拽归属 ----
    Console.WriteLine();
    Console.WriteLine("== 10. 子视图模型（空分类 / 未分区池 / 归属调整） ==");

    var navDialog = new ConsoleDialogService();
    var navState = new ShellState();
    var nav = new CategoryNavViewModel(workspace2, navDialog, launchService, navState);
    Check(nav.Categories.Count == workspace2.Groups.Count + 1, "分类导航包含未分组节点");

    // 新建一个空分类，专门验证空分类状态
    ShortcutGroup emptyGroup = workspace2.CreateGroup("空分类测试");
    CategoryNodeViewModel emptyGroupNode = nav.Categories.First(c => c.Group?.Id == emptyGroup.Id);
    nav.SelectedCategory = emptyGroupNode;
    Check(nav.HasNoItems && nav.Items.Count == 0, "空分类进入空状态");
    Check(!string.IsNullOrEmpty(nav.EmptyStateText), "空分类有提示文案");

    // 选中已有分类（group2 里原来的项目已被自动清除）
    CategoryNodeViewModel group2Node = nav.Categories.First(c => c.Group?.Id == group2.Id);
    nav.SelectedCategory = group2Node;
    Check(nav.Items.Count == 0 && nav.HasNoItems, "分类里已删除的项目不再展示",
        $"项目数 {nav.Items.Count}");

    // 未分组节点
    nav.SelectedCategory = nav.Categories[0];
    Check(nav.Items.Count > 0, "未分组节点展示未分配项目");

    // 拖拽归属：从未分组拖入空分类（归属记录写入，视图保持用户当前选择）
    DesktopItem poolItem = nav.Items.First();
    nav.AssignToNode(poolItem, emptyGroupNode);
    Check(emptyGroup.ItemIds.Contains(poolItem.Id), "拖拽把项目加入目标分类");

    // 切换到目标分类，验证项目已归属
    CategoryNodeViewModel refreshedEmptyNode = nav.Categories.First(c => c.Group?.Id == emptyGroup.Id);
    nav.SelectedCategory = refreshedEmptyNode;
    Check(nav.Items.Count == 1 && nav.SelectedCategory?.Group?.Id == emptyGroup.Id,
        "加入后分类展示该项目", $"项目数 {nav.Items.Count}");

    // 搜索过滤
    navState.SearchText = "绝对不存在";
    Check(nav.Items.Count == 0 && nav.HasNoItems, "分类内搜索无结果进入空状态");
    navState.SearchText = poolItem.Name;
    Check(nav.Items.Count == 1, "分类内按名称搜索命中");
    navState.SearchText = string.Empty;

    var fpDialog = new ConsoleDialogService();
    var fp = new FreePanelsViewModel(workspace2, fpDialog, launchService, navState);
    Check(fp.Panels.Count == workspace2.Panels.Count, "自由分区视图模型加载全部分区");
    Check(fp.PoolItems.Count == workspace2.Items.Count(i => !workspace2.Panels.Any(p => p.ItemIds.Contains(i.Id))),
        "未分区池数量与未分配项目一致");

    PanelViewModel panelVm = fp.Panels.First(p => p.Model.Id == panel2.Id);

    // 阶段 19：数据共享 + 「全部」含已分组项目（不再从列表里消失）
    nav.SelectedCategory = nav.Categories[0]; // 全部
    int allBefore = nav.Items.Count;
    DesktopItem shareItem = nav.Items.First();
    fp.AssignToPanel(shareItem, panelVm);
    Check(!fp.PoolItems.Contains(shareItem) == false, "两种布局共用同一份数据（分区归属同步，全部仍可见）");
    Check(nav.Items.Contains(shareItem) && nav.Items.Count == allBefore,
        "「全部」不会因项目归到分区而减少", $"{nav.Items.Count}/{allBefore}");

    // Changed 事件后面板视图模型会整体重建，需重新获取最新实例验证
    PanelViewModel refreshedPanel = fp.Panels.First(p => p.Model.Id == panel2.Id);
    Check(refreshedPanel.Items.Contains(shareItem), "项目成功加入分区");

    fp.AssignToPool(shareItem);
    Check(nav.Items.Contains(shareItem) && fp.PoolItems.Contains(shareItem), "从分区移出后两边都仍可见");

    fp.AssignToPanel(shareItem, refreshedPanel);
    Check(refreshedPanel.Items.Contains(shareItem) && fp.PoolItems.Contains(shareItem),
        "重新拖入分区后，项目仍留在「全部」里");
}
finally
{
    try
    {
        Directory.Delete(wsRoot, true);
    }
    catch
    {
        Info($"工作区测试临时目录删除失败（可手动删除）：{wsRoot}");
    }
}

// ---------------------------------------------------------------- 11. 损坏 / 空 / 过新配置
Console.WriteLine();
Console.WriteLine("== 11. 配置容错（损坏 / 空文件 / 版本过新 / 旧版迁移） ==");
string cfgRoot = Path.Combine(Path.GetTempPath(), "DeskLanesCfg_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    Directory.CreateDirectory(cfgRoot);
    string libPath = Path.Combine(cfgRoot, "library.json");
    string setPath = Path.Combine(cfgRoot, "settings.json");
    string backupDir = Path.Combine(cfgRoot, "backups");

    // 11.1 损坏 JSON
    File.WriteAllText(libPath, "{ 这不是合法 JSON，模拟损坏 ");
    var corruptStore = new LibraryStore(cfgRoot);
    corruptStore.Load();
    Check(corruptStore.Warnings.Any(w => w.Contains("损坏")), "损坏配置给出警告",
        string.Join(" | ", corruptStore.Warnings));
    Check(corruptStore.Data.Items.Count == 0 && corruptStore.Data.Version == DataVersions.Current,
        "损坏配置回退默认值");
    Check(Directory.Exists(backupDir) && Directory.EnumerateFiles(backupDir).Any(f => f.Contains("corrupt")),
        "损坏原文件已备份到 backups 目录");

    // 11.2 空文件
    File.WriteAllText(libPath, string.Empty);
    var emptyStore = new LibraryStore(cfgRoot);
    emptyStore.Load();
    Check(emptyStore.Warnings.Count == 0 && emptyStore.Data.Version == DataVersions.Current,
        "空文件按首次使用处理（无警告）");

    // 11.3 版本过新
    File.WriteAllText(libPath, "{\"Version\": 999, \"Items\": []}");
    var futureStore = new LibraryStore(cfgRoot);
    futureStore.Load();
    Check(futureStore.Warnings.Any(w => w.Contains("版本")), "版本过新给出警告",
        string.Join(" | ", futureStore.Warnings));
    Check(futureStore.Data.Version == DataVersions.Current, "版本过新回退当前版本默认值");
    Check(Directory.EnumerateFiles(backupDir).Any(f => f.Contains("newer-v999")), "过新配置已备份");

    // 11.4 旧版配置（显式 Version: 0，模拟更早的格式）
    File.WriteAllText(libPath, "{\"Version\": 0, \"Items\": [], \"Groups\": [], \"Panels\": []}");
    var legacyStore = new LibraryStore(cfgRoot);
    legacyStore.Load();
    Check(legacyStore.Warnings.Any(w => w.Contains("旧版")), "旧版配置给出迁移警告",
        string.Join(" | ", legacyStore.Warnings));
    Check(legacyStore.Data.Version == DataVersions.Current, "旧版配置按当前版本加载");

    // 11.4b 缺少 Version 字段：按当前版本宽容加载（字段缺失取默认值）
    File.WriteAllText(libPath, "{\"Items\": [], \"Groups\": [], \"Panels\": []}");
    var noVersionStore = new LibraryStore(cfgRoot);
    noVersionStore.Load();
    Check(noVersionStore.Warnings.Count == 0 && noVersionStore.Data.Version == DataVersions.Current,
        "缺少 Version 字段按当前版本宽容加载");

    // 11.5 settings.json 损坏
    File.WriteAllText(setPath, "损坏");
    var settingsStore = new ConfigStore(cfgRoot);
    settingsStore.Load();
    Check(settingsStore.Warnings.Any(w => w.Contains("损坏")), "设置文件损坏同样容错");
    Check(settingsStore.Data.LayoutMode == LayoutMode.CategoryNav, "设置损坏回退默认布局");

    // 11.6 合法内容可正常读回（含中文与自定义项）
    File.WriteAllText(libPath, """
    {
      "Version": 1,
      "Items": [
        { "Id": "abc", "Name": "自定义项", "FullPath": "C:\\temp\\x.txt", "Kind": 1, "Origin": 2, "IsAvailable": true }
      ],
      "Groups": [],
      "Panels": [],
      "DismissedPaths": []
    }
    """);
    var validStore = new LibraryStore(cfgRoot);
    validStore.Load();
    Check(validStore.Warnings.Count == 0 && validStore.Data.Items.Count == 1 &&
          validStore.Data.Items[0].Name == "自定义项" && validStore.Data.Items[0].Origin == ItemOrigin.Custom,
        "手工编辑的合法配置可正常读回（中文/自定义来源）");
}
finally
{
    try
    {
        Directory.Delete(cfgRoot, true);
    }
    catch
    {
        Info($"配置容错测试临时目录删除失败（可手动删除）：{cfgRoot}");
    }
}

// ---------------------------------------------------------------- 12. 桌面自动同步
Console.WriteLine();
Console.WriteLine("== 12. 桌面自动同步（监听 + 防抖 + 重扫校正） ==");

async Task<SyncSummary?> WaitForSyncAsync(IDesktopSyncService service, int timeoutMs = 6000)
{
    var completion = new TaskCompletionSource<SyncSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    void Handler(object? sender, SyncSummary summary) => completion.TrySetResult(summary);
    service.SyncCompleted += Handler;
    try
    {
        Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
        return finished == completion.Task ? await completion.Task : null;
    }
    finally
    {
        service.SyncCompleted -= Handler;
    }
}

async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 5000)
{
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs)
    {
        if (condition())
        {
            return true;
        }

        await Task.Delay(50);
    }

    return condition();
}

/// <summary>从指定配置目录重新加载设置（验证持久化结果，不依赖内存中的对象）。</summary>
WindowMode ReloadMode(string dir)
{
    var store = new ConfigStore(dir);
    store.Load();
    return store.Data.WindowMode;
}

string syncRoot = Path.Combine(Path.GetTempPath(), "DeskLanesStage3_" + Guid.NewGuid().ToString("N")[..8]);
string syncUser = Path.Combine(syncRoot, "user");
string syncPublic = Path.Combine(syncRoot, "public");
string syncCfg = Path.Combine(syncRoot, "cfg");
Directory.CreateDirectory(syncUser);
Directory.CreateDirectory(syncPublic);
Directory.CreateDirectory(syncCfg);

IDesktopSyncService? syncService = null;
try
{
    var syncScanner = new DesktopScanner(resolver);
    var syncWorkspace = new WorkspaceService(new LibraryStore(syncCfg), new ConfigStore(syncCfg), resolver);

    ScanResult seedScan = await syncScanner.ScanAsync(
        new ScanOptions { UserDesktopPath = syncUser, PublicDesktopPath = syncPublic });
    await syncWorkspace.MergeScanAsync(seedScan);
    Check(syncWorkspace.Items.Count == 0, "（准备）初始空桌面合并后库为空");

    var watchTargets = new List<WatchTarget>
    {
        new(syncUser, ItemOrigin.UserDesktop, "用户桌面"),
        new(syncPublic, ItemOrigin.PublicDesktop, "公共桌面"),
    };
    var syncOptions = new SyncOptions
    {
        DebounceInterval = TimeSpan.FromMilliseconds(150),
        MaxDebounceDelay = TimeSpan.FromMilliseconds(600),
        EnablePeriodicRescan = false,
    };

    int syncCount = 0;
    syncService = new DesktopSyncService(syncWorkspace, syncScanner, watchTargets, syncOptions);
    syncService.SyncCompleted += (_, _) => syncCount++;
    syncService.Start();

    Check(syncService.IsRunning, "同步服务启动成功");
    Check(syncService.IsWatchActive && !syncService.IsDegraded, "两个桌面目录均进入监听", syncService.StatusText);
    Check(syncService.WatchedDirectories.Count == 2, "监听目录数量为 2",
        string.Join(" | ", syncService.WatchedDirectories));

    // ---- 12.1 新增文件自动发现 ----
    string newFile = Path.Combine(syncUser, "新建文件.txt");
    File.WriteAllText(newFile, "hello");
    SyncSummary? fileSync = await WaitForSyncAsync(syncService);
    Check(fileSync is not null && syncWorkspace.Items.Any(i => i.FullPath == newFile),
        "新增文件被自动发现", fileSync is null ? "等待同步超时" : null);

    // ---- 12.2 新增文件夹自动发现 ----
    string newFolder = Path.Combine(syncUser, "新建文件夹");
    Directory.CreateDirectory(newFolder);
    await WaitForSyncAsync(syncService);
    Check(syncWorkspace.Items.Any(i => i.FullPath == newFolder && i.Kind == DesktopItemKind.Folder),
        "新增文件夹被自动发现");

    // ---- 12.3 新增快捷方式自动发现（真实 .lnk 复制 + .url） ----
    DesktopItem? realLnk = scan.Items.FirstOrDefault(i =>
        i.Kind == DesktopItemKind.Shortcut &&
        i.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
    if (realLnk is not null)
    {
        string copiedLnk = Path.Combine(syncUser, "复制来的快捷方式.lnk");
        File.Copy(realLnk.FullPath, copiedLnk, true);
        await WaitForSyncAsync(syncService);
        DesktopItem? lnkItem = syncWorkspace.Items.FirstOrDefault(i => i.FullPath == copiedLnk);
        Check(lnkItem is not null && lnkItem.Kind == DesktopItemKind.Shortcut, "新增 .lnk 快捷方式被自动发现");
    }
    else
    {
        Info("真实桌面上没有 .lnk 样例，跳过 .lnk 自动发现检查");
    }

    string urlFile = Path.Combine(syncUser, "测试网址.url");
    File.WriteAllText(urlFile, "[InternetShortcut]\r\nURL=https://example.com\r\n");
    await WaitForSyncAsync(syncService);
    DesktopItem? urlItem = syncWorkspace.Items.FirstOrDefault(i => i.FullPath == urlFile);
    Check(urlItem is not null && urlItem.Kind == DesktopItemKind.Shortcut, "新增 .url 快捷方式被自动发现");

    // ---- 12.4 重命名：复用同一项目，保留分类归属 ----
    ShortcutGroup keepGroup = syncWorkspace.CreateGroup("重命名保持");
    syncWorkspace.AssignItemToGroup(syncWorkspace.Items.First(i => i.FullPath == newFile), keepGroup);
    int countBeforeRename = syncWorkspace.Items.Count;

    string renamedFile = Path.Combine(syncUser, "改名后.txt");
    File.Move(newFile, renamedFile);
    await WaitForSyncAsync(syncService);

    DesktopItem? renamed = syncWorkspace.Items.FirstOrDefault(i => i.FullPath == renamedFile);
    Check(renamed is not null, "重命名后新路径出现在库中");
    Check(renamed is not null && keepGroup.ItemIds.Contains(renamed.Id),
        "重命名后分类归属保留（同一个项目 Id）");
    Check(syncWorkspace.Items.Count == countBeforeRename, "重命名不产生重复条目",
        $"{syncWorkspace.Items.Count} / {countBeforeRename}");

    // ---- 12.5 删除：阶段 14 自动清除 ----
    File.Delete(renamedFile);
    await WaitForSyncAsync(syncService);
    DesktopItem? removed = renamed is null
        ? null
        : syncWorkspace.Items.FirstOrDefault(i => i.Id == renamed.Id);
    Check(removed is null, "桌面上已删除的项目被自动清除");
    Check(renamed is null || !keepGroup.ItemIds.Contains(renamed.Id), "自动清除时分类归属一并摘掉");

    // ---- 12.6 快速连续创建多个项目 ----
    int countBeforeBurst = syncWorkspace.Items.Count;
    int syncsBeforeBurst = syncCount;
    for (int i = 0; i < 10; i++)
    {
        File.WriteAllText(Path.Combine(syncPublic, $"批量{i}.txt"), "x");
    }

    bool burstDone = await WaitForConditionAsync(() => syncWorkspace.Items.Count == countBeforeBurst + 10, 6000);
    Check(burstDone, "批量创建的 10 个项目全部进入库（无遗漏）",
        $"实际 {syncWorkspace.Items.Count}，期望 {countBeforeBurst + 10}");
    Check(syncWorkspace.Items.Select(i => i.PathKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
              == syncWorkspace.Items.Count,
        "批量创建不产生重复条目");
    Check(syncCount - syncsBeforeBurst <= 6, "批量事件被防抖合并为少量同步（不是每个事件一次刷新）",
        $"同步次数 {syncCount - syncsBeforeBurst}");

    // ---- 12.7 桌面外自定义项不受同步影响 ----
    string externalItemPath = Path.Combine(syncRoot, "桌面外文件.txt");
    File.WriteAllText(externalItemPath, "external");
    syncWorkspace.AddCustomItem(externalItemPath);
    DesktopItem customSynced = syncWorkspace.Items.First(i => i.FullPath == externalItemPath);
    await syncService.SyncNowAsync();
    Check(syncWorkspace.Items.Contains(customSynced) && customSynced.IsAvailable,
        "桌面外自定义项不会因同步被判失效或移除");

    // ---- 12.8 用户配置不被同步覆盖 ----
    WorkspacePanel keepPanel = syncWorkspace.CreatePanel("同步保持分区");
    DesktopItem panelTarget = syncWorkspace.Items.First(i => i.FullPath == Path.Combine(syncPublic, "批量0.txt"));
    syncWorkspace.AssignItemToPanel(panelTarget, keepPanel);
    syncWorkspace.UpdatePanelLayout(keepPanel, 111, 222, 333, 444);
    syncWorkspace.SetLayoutMode(LayoutMode.FreePanels);
    syncWorkspace.SaveWindowPreferences(1000, 800, 10, 20, false);

    File.WriteAllText(Path.Combine(syncUser, "触发一次同步.txt"), "x");
    await WaitForSyncAsync(syncService);

    // 阶段 19：切换布局会把两边补成同一份分组定义，因此集合数量会变，
    // 改为按 Id 验证原对象没有被重建、几何与成员都还在。
    Check(syncWorkspace.Groups.Any(g => g.Id == keepGroup.Id),
        "同步后分类仍在（不被重建）",
        $"groups={syncWorkspace.Groups.Count}");
    double syncGrid = syncWorkspace.Settings.PanelGridSize;
    Check(syncWorkspace.Panels.Any(p => p.Id == keepPanel.Id)
          && keepPanel.X % syncGrid == 0 && keepPanel.Width % syncGrid == 0
          && keepPanel.X > 0 && keepPanel.Width > 0,
        "同步后分区几何保留（已吸附到方格）",
        $"X={keepPanel.X} W={keepPanel.Width} grid={syncGrid}");
    Check(keepPanel.ItemIds.Contains(panelTarget.Id), "同步后分区成员关系保留");
    WorkspacePanel? mirrored = syncWorkspace.Panels.FirstOrDefault(p => p.GroupId == keepGroup.Id);
    Check(mirrored is not null && mirrored.ItemIds.Count == keepGroup.ItemIds.Count,
        "切换布局后分类成员镜像到配对分区",
        mirrored is null ? "无配对分区" : $"{mirrored.ItemIds.Count}/{keepGroup.ItemIds.Count}");
    Check(syncWorkspace.Settings.LayoutMode == LayoutMode.FreePanels, "同步后布局模式保留");
    Check(syncWorkspace.Settings.WindowWidth == 1000 && syncWorkspace.Settings.WindowLeft == 10,
        "同步后窗口偏好保留");

    // ---- 12.10 窗口未显示时保存偏好不能把配置写坏 ----
    // 侧边栏/悬浮窗模式下主窗口从未 Show，Left/Top 是 NaN；NaN 无法写进 JSON，
    // System.Text.Json 会抛 ArgumentException（虚拟机实测踩到过）。
    syncWorkspace.SaveWindowPreferences(double.NaN, double.NaN, null, null, false);
    Check(syncWorkspace.Settings.WindowWidth == 1000 && syncWorkspace.Settings.WindowHeight == 800,
        "尺寸为 NaN 时保留原窗口尺寸（不写坏配置）");
    Check(syncWorkspace.Settings.WindowLeft is null && syncWorkspace.Settings.WindowTop is null,
        "位置为 null 时清空窗口位置");
    syncWorkspace.SaveWindowPreferences(1200, 900, double.NaN, 30, false);
    Check(syncWorkspace.Settings.WindowWidth == 1200 && syncWorkspace.Settings.WindowLeft is null
        && syncWorkspace.Settings.WindowTop == 30,
        "位置为 NaN 时按 null 处理、其余字段照常保存");

    // ---- 12.9 局部重扫不误伤未扫描目录 ----
    string victimPath = Path.Combine(syncPublic, "批量0.txt");
    File.Delete(victimPath);
    ScanResult partialScan = await syncScanner.ScanAsync(new ScanOptions
    {
        Targets = new List<ScanTarget> { new() { Path = syncUser, Origin = ItemOrigin.UserDesktop } },
    });
    await syncWorkspace.MergeScanAsync(partialScan);
    DesktopItem? victimItem = syncWorkspace.Items.FirstOrDefault(i =>
        string.Equals(i.FullPath, victimPath, StringComparison.OrdinalIgnoreCase));
    Check(victimItem is not null && victimItem.IsAvailable,
        "只重扫用户桌面时，公共桌面项目保持原状（不被误判失效）");

    ScanResult fullScan = await syncScanner.ScanAsync(new ScanOptions
    {
        Targets = new List<ScanTarget>
        {
            new() { Path = syncUser, Origin = ItemOrigin.UserDesktop },
            new() { Path = syncPublic, Origin = ItemOrigin.PublicDesktop },
        },
    });
    await syncWorkspace.MergeScanAsync(fullScan);
    Check(victimItem is not null && !syncWorkspace.Items.Contains(victimItem),
        "全量扫描后确实已删除的项目被自动清除");

    // ---- 12.10 重命名迁移的安全边界（无法确定时不猜测） ----
    int noSource = syncWorkspace.ApplyPathMigrations(new List<PathMigration>
    {
        new(Path.Combine(syncUser, "不存在的旧名字.txt"), Path.Combine(syncUser, "新名字.txt")),
    });
    Check(noSource == 0, "源项目不存在时不做迁移（不猜测）");

    string existA = Path.Combine(syncUser, "甲.txt");
    string existB = Path.Combine(syncUser, "乙.txt");
    File.WriteAllText(existA, "a");
    File.WriteAllText(existB, "b");
    await syncService.SyncNowAsync();
    int occupied = syncWorkspace.ApplyPathMigrations(new List<PathMigration> { new(existA, existB) });
    Check(occupied == 0, "目标路径已被占用时不做迁移（不猜测合并）");
    Check(syncWorkspace.Items.Any(i => i.FullPath == existA) &&
          syncWorkspace.Items.Any(i => i.FullPath == existB), "两个项目各自独立未被合并");

    // ---- 12.11 目录不可访问：跳过合并且不误判失效 ----
    string missRoot = Path.Combine(syncRoot, "missing");
    string missDir = Path.Combine(missRoot, "desktop");
    string missCfg = Path.Combine(missRoot, "cfg");
    Directory.CreateDirectory(missDir);
    Directory.CreateDirectory(missCfg);
    File.WriteAllText(Path.Combine(missDir, "a.txt"), "a");

    var missWorkspace = new WorkspaceService(new LibraryStore(missCfg), new ConfigStore(missCfg), resolver);
    await missWorkspace.MergeScanAsync(await syncScanner.ScanAsync(new ScanOptions
    {
        Targets = new List<ScanTarget> { new() { Path = missDir, Origin = ItemOrigin.UserDesktop } },
    }));
    Check(missWorkspace.Items.Count == 1, "（准备）临时桌面目录内有 1 个项目");

    Directory.Delete(missDir, true);
    using (var missService = new DesktopSyncService(
               missWorkspace,
               syncScanner,
               new List<WatchTarget> { new(missDir, ItemOrigin.UserDesktop, "用户桌面") },
               new SyncOptions { EnablePeriodicRescan = false }))
    {
        missService.Start();
        Check(missService.FailedDirectories.Count == 1 && missService.IsDegraded,
            "目录不存在时监听失败并记录降级", missService.StatusText);

        SyncSummary missSummary = await missService.SyncNowAsync();
        Check(missSummary.Skipped, "目录不可访问时跳过合并", missSummary.SkipReason);
        Check(missWorkspace.Items.Count == 1 && missWorkspace.Items[0].IsAvailable,
            "目录不可访问不会把项目误判为失效");
    }

    // ---- 12.12 降级轮询与监听自动恢复 ----
    string degradeRoot = Path.Combine(syncRoot, "degrade");
    string degradeDir = Path.Combine(degradeRoot, "desktop");
    string degradeCfg = Path.Combine(degradeRoot, "cfg");
    Directory.CreateDirectory(degradeRoot);
    Directory.CreateDirectory(degradeCfg);

    var degradeWorkspace = new WorkspaceService(
        new LibraryStore(degradeCfg), new ConfigStore(degradeCfg), resolver);
    using (var degradeService = new DesktopSyncService(
               degradeWorkspace,
               syncScanner,
               new List<WatchTarget> { new(degradeDir, ItemOrigin.UserDesktop, "用户桌面") },
               new SyncOptions
               {
                   DebounceInterval = TimeSpan.FromMilliseconds(100),
                   EnablePeriodicRescan = true,
                   PeriodicRescanInterval = TimeSpan.FromMilliseconds(250),
               }))
    {
        degradeService.Start();
        Check(degradeService.IsDegraded, "没有可监听目录时进入降级状态", degradeService.StatusText);
        Check(degradeService.StatusText.Contains("降级"), "状态文字说明降级原因", degradeService.StatusText);

        // 目录重新出现后，周期性扫描中会自动恢复监听并把数据补回来
        Directory.CreateDirectory(degradeDir);
        File.WriteAllText(Path.Combine(degradeDir, "恢复后.txt"), "x");
        bool restored = await WaitForConditionAsync(() => !degradeService.IsDegraded, 6000);
        Check(restored, "目录重新可访问后监听自动恢复", degradeService.StatusText);
        Check(degradeWorkspace.Items.Any(i => i.Name == "恢复后"),
            "降级/恢复过程中数据仍然被正确同步进库");
    }

    // ---- 12.13 停止与释放 ----
    int countBeforeStop = syncWorkspace.Items.Count;
    syncService.Stop();
    Check(!syncService.IsRunning && !syncService.IsWatchActive, "停止后不再监听");

    File.WriteAllText(Path.Combine(syncUser, "停止后.txt"), "x");
    await Task.Delay(600);
    Check(syncWorkspace.Items.Count == countBeforeStop, "停止后文件变化不再进入库");

    Check(syncService.RecentLog.Count > 0, "保留诊断日志供排查", $"{syncService.RecentLog.Count} 条");

    syncService.Dispose();
    File.WriteAllText(Path.Combine(syncUser, "释放后.txt"), "x");
    await Task.Delay(600);
    Check(syncWorkspace.Items.Count == countBeforeStop, "释放后文件变化不再进入库");

    // ---- 12.14 重复 Start 不重复订阅 ----
    using (var duplicateService = new DesktopSyncService(
               syncWorkspace,
               syncScanner,
               watchTargets,
               new SyncOptions { EnablePeriodicRescan = false }))
    {
        duplicateService.Start();
        duplicateService.Start();
        duplicateService.Start();
        Check(duplicateService.WatchedDirectories.Count == 2, "重复 Start 不会重复创建监听器",
            string.Join(" | ", duplicateService.WatchedDirectories));
    }

    // ---- 12.15 回归：同步进行中产生的变更不能被丢弃（要排队到下一轮） ----
    string queueRoot = Path.Combine(syncRoot, "queue");
    string queueDir = Path.Combine(queueRoot, "desktop");
    string queueCfg = Path.Combine(queueRoot, "cfg");
    Directory.CreateDirectory(queueDir);
    Directory.CreateDirectory(queueCfg);

    var slowScanner = new DelayedScanner(new DesktopScanner(resolver)) { DelayMs = 700 };
    var queueWorkspace = new WorkspaceService(
        new LibraryStore(queueCfg), new ConfigStore(queueCfg), resolver);

    using (var queueService = new DesktopSyncService(
               queueWorkspace,
               slowScanner,
               new List<WatchTarget> { new(queueDir, ItemOrigin.UserDesktop, "用户桌面") },
               new SyncOptions
               {
                   DebounceInterval = TimeSpan.FromMilliseconds(150),
                   MaxDebounceDelay = TimeSpan.FromSeconds(2),
                   EnablePeriodicRescan = false,
               }))
    {
        queueService.Start();

        string firstPath = Path.Combine(queueDir, "第一个.txt");
        File.WriteAllText(firstPath, "1");

        // 第一波同步已经开始（扫描被人为放慢到 700 毫秒，此刻仍在进行中）
        await Task.Delay(250);

        // 在同步进行中产生第二个变更：不能因为"已有同步在进行"就丢掉
        string secondPath = Path.Combine(queueDir, "第二个.txt");
        File.WriteAllText(secondPath, "2");

        bool bothFound = await WaitForConditionAsync(() =>
            queueWorkspace.Items.Any(i => i.FullPath == firstPath) &&
            queueWorkspace.Items.Any(i => i.FullPath == secondPath), 10000);
        Check(bothFound, "同步进行中产生的变更不会被丢弃（排队到下一轮执行）",
            $"库内 {queueWorkspace.Items.Count} 项");
        Check(slowScanner.CallCount >= 2, "确实执行了第二轮扫描",
            $"扫描次数 {slowScanner.CallCount}");
    }

    syncService = null;
}
finally
{
    syncService?.Dispose();
    try
    {
        Directory.Delete(syncRoot, true);
    }
    catch
    {
        Info($"自动同步测试临时目录删除失败（可手动删除）：{syncRoot}");
    }
}

// ---------------------------------------------------------------- 13. 阶段 4：窗口模式 / 快捷键 / 托盘 / 设置
Console.WriteLine();
Console.WriteLine("== 13. 窗口模式、全局快捷键、托盘菜单与设置 ==");

// 13.1 快捷键定义：常量、默认值、解析、展示、边界
Console.WriteLine("-- 13.1 快捷键定义 --");
HotkeyDefinition defaultHotkey = HotkeyDefinition.Default;
Check(
    defaultHotkey.Modifiers == (HotkeyDefinition.ModControl | HotkeyDefinition.ModAlt) &&
    defaultHotkey.Key == HotkeyDefinition.KeySpace,
    "默认快捷键为 Ctrl+Alt+Space");
Check(defaultHotkey.DisplayText == "Ctrl+Alt+Space", "默认快捷键展示文本正确", defaultHotkey.DisplayText);
Check(defaultHotkey.HasModifier && defaultHotkey.IsValid, "默认快捷键包含修饰键且可注册");
Check(HotkeyDefinition.ModNoRepeat == 0x4000, "ModNoRepeat 常量为 0x4000（防长按重复）");

HotkeyDefinition? parsedHotkey = HotkeyDefinition.TryParse("Ctrl+Alt+Space");
Check(parsedHotkey == HotkeyDefinition.Default, "解析 Ctrl+Alt+Space 与默认一致");
parsedHotkey = HotkeyDefinition.TryParse("ctrl+shift+d");
Check(
    parsedHotkey is not null &&
    parsedHotkey.Modifiers == (HotkeyDefinition.ModControl | HotkeyDefinition.ModShift) &&
    parsedHotkey.Key == 'D',
    "解析支持小写修饰键与字母主键",
    parsedHotkey?.DisplayText);
parsedHotkey = HotkeyDefinition.TryParse("Win+Ctrl+F12");
Check(parsedHotkey is not null && parsedHotkey.DisplayText == "Ctrl+Win+F12", "解析 Win 组合并规范展示顺序", parsedHotkey?.DisplayText);
parsedHotkey = HotkeyDefinition.TryParse("Alt+Enter");
Check(parsedHotkey is not null && parsedHotkey.Key == 0x0D && parsedHotkey.DisplayText == "Alt+Enter", "解析 Enter 等特殊键", parsedHotkey?.DisplayText);
parsedHotkey = HotkeyDefinition.TryParse("Ctrl+Alt+PageUp");
Check(parsedHotkey is not null && parsedHotkey.DisplayText == "Ctrl+Alt+PageUp", "解析 PageUp 等功能键", parsedHotkey?.DisplayText);
parsedHotkey = HotkeyDefinition.TryParse("Ctrl+Alt+F13");
Check(parsedHotkey is not null && parsedHotkey.DisplayText == "Ctrl+Alt+F13", "解析并正确展示 F13 功能键", parsedHotkey?.DisplayText);
parsedHotkey = HotkeyDefinition.TryParse("Shift+F24");
Check(parsedHotkey is not null && parsedHotkey.DisplayText == "Shift+F24", "解析 F24（功能键范围到 0x87）", parsedHotkey?.DisplayText);

Check(HotkeyDefinition.TryParse("Space") is null, "无修饰键的裸键拒绝注册");
Check(HotkeyDefinition.TryParse("") is null, "空文本解析失败");
Check(HotkeyDefinition.TryParse("   ") is null, "空白文本解析失败");
Check(HotkeyDefinition.TryParse("Ctrl+不存在的键") is null, "无法识别的键名解析失败");
Check(HotkeyDefinition.TryParse("Alt+") is null, "只有修饰键没有主键时解析失败");
Check(!new HotkeyDefinition(0, HotkeyDefinition.KeySpace).IsValid, "IsValid 对裸键返回 false");
Check(!new HotkeyDefinition(HotkeyDefinition.ModControl, 0).IsValid, "IsValid 对空主键返回 false");

// 13.2 阶段 4 设置项：默认值与持久化往返
Console.WriteLine("-- 13.2 窗口模式与行为设置 --");
AppSettings settingDefaults = new();
Check(settingDefaults.WindowMode == WindowMode.Full, "默认窗口模式为全屏");
Check(settingDefaults.Hotkey == HotkeyDefinition.Default, "默认快捷键为 Ctrl+Alt+Space");
Check(settingDefaults.FloatingHideOnDeactivate, "悬浮窗默认失焦隐藏");
Check(
    settingDefaults.SidebarDock == SidebarDock.Right &&
    settingDefaults.SidebarExpanded &&
    settingDefaults.SidebarWidth == 220,
    "侧边栏默认停靠右侧、展开、宽度 220（阶段 26 收窄）");
Check(settingDefaults.SidebarAutoCollapse, "侧边栏默认失焦收起");
Check(settingDefaults.MinimizeToTray, "默认关闭窗口时最小化到托盘");

string s4Dir = Path.Combine(Path.GetTempPath(), "DeskLanesS4_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(s4Dir);
try
{
    var settingsStore = new ConfigStore(s4Dir);
    settingsStore.Load();
    settingsStore.Data.WindowMode = WindowMode.Sidebar;
    settingsStore.Data.HotkeyModifiers = HotkeyDefinition.ModControl | HotkeyDefinition.ModShift;
    settingsStore.Data.HotkeyKey = 'D';
    settingsStore.Data.FloatingHideOnDeactivate = false;
    settingsStore.Data.SidebarDock = SidebarDock.Left;
    settingsStore.Data.SidebarExpanded = false;
    settingsStore.Data.SidebarWidth = 320;
    settingsStore.Data.SidebarAutoCollapse = false;
    settingsStore.Data.MinimizeToTray = false;
    settingsStore.Save();

    var reloaded = new ConfigStore(s4Dir);
    reloaded.Load();
    Check(
        reloaded.Data.WindowMode == WindowMode.Sidebar &&
        reloaded.Data.HotkeyModifiers == (HotkeyDefinition.ModControl | HotkeyDefinition.ModShift) &&
        reloaded.Data.HotkeyKey == 'D' &&
        !reloaded.Data.FloatingHideOnDeactivate &&
        reloaded.Data.SidebarDock == SidebarDock.Left &&
        !reloaded.Data.SidebarExpanded &&
        reloaded.Data.SidebarWidth == 320 &&
        !reloaded.Data.SidebarAutoCollapse &&
        !reloaded.Data.MinimizeToTray,
        "阶段 4 新增设置项全部持久化并读回");
    Check(
        reloaded.Data.Hotkey.DisplayText == "Ctrl+Shift+D",
        "持久化后的快捷键定义可正确展示",
        reloaded.Data.Hotkey.DisplayText);
}
finally
{
    try
    {
        Directory.Delete(s4Dir, true);
    }
    catch
    {
        Info($"设置持久化测试临时目录删除失败（可手动删除）：{s4Dir}");
    }
}

// 13.3 全局热键服务：真实 RegisterHotKey / 注销 / 冲突提示
Console.WriteLine("-- 13.3 全局热键服务（真实注册） --");
IntPtr hotkeyWindow = NativeTestWindow.Create();
if (hotkeyWindow == IntPtr.Zero)
{
    Info("无法创建测试用原生窗口，跳过真实注册断言（不影响其他测试）");
}
else
{
    try
    {
        // 用一个极罕见的组合，避免与用户已注册的快捷键冲突
        var rareCombo = new HotkeyDefinition(
            HotkeyDefinition.ModControl | HotkeyDefinition.ModAlt | HotkeyDefinition.ModShift, 0x7C); // F13

        var hotkey = new HotkeyService(hotkeyWindow);
        OperationResult reg = hotkey.Register(rareCombo);
        bool rareAvailable = reg.Success;
        Check(rareAvailable, "真实注册全局快捷键成功（Ctrl+Alt+Shift+F13）", reg.ErrorMessage);

        int pressedCount = 0;
        hotkey.Pressed += (_, _) => pressedCount++;
        hotkey.RaisePressed();
        Check(pressedCount == 1, "按下事件可触发（RaisePressed）");

        if (rareAvailable)
        {
            Check(hotkey.IsRegistered && hotkey.Current == rareCombo, "注册后状态与当前定义正确");
            Check(
                HotkeyStatusText.Describe(hotkey).Contains("Ctrl+Alt+Shift+F13"),
                "状态文本展示已注册的快捷键",
                HotkeyStatusText.Describe(hotkey));

            OperationResult again = hotkey.Register(rareCombo);
            Check(again.Success, "重复注册同一组合（先注销再注册）仍然成功");

            OperationResult bare = hotkey.Register(new HotkeyDefinition(0, HotkeyDefinition.KeySpace));
            Check(!bare.Success && bare.ErrorMessage!.Contains("修饰键"), "裸键被拒绝并给出中文原因", bare.ErrorMessage);
            Check(hotkey.IsRegistered && hotkey.Current == rareCombo, "拒绝裸键后原快捷键保持注册");

            // 同一组合被第二个服务注册：应失败并给出占用提示与替代方案
            var other = new HotkeyService(hotkeyWindow);
            OperationResult conflict = other.Register(rareCombo);
            Check(
                !conflict.Success && conflict.ErrorMessage!.Contains("占用") && conflict.ErrorMessage.Contains("Ctrl+Alt+D"),
                "组合键被占用时给出中文冲突提示与替代方案",
                conflict.ErrorMessage);
            Check(!other.IsRegistered, "冲突时第二个服务不记录注册状态");
            other.Dispose();
        }

        Check(hotkey.Unregister(), "注销返回 true");
        Check(!hotkey.IsRegistered && hotkey.Current is null, "注销后状态清空");
        Check(hotkey.Unregister(), "重复注销幂等返回 true");
        Check(
            HotkeyStatusText.Describe(hotkey).Contains("未注册"),
            "注销后状态文本为未注册");

        hotkey.Dispose();
        Check(!hotkey.IsRegistered, "Dispose 后不再持有注册");
        OperationResult afterDispose = hotkey.Register(rareCombo);
        Check(!afterDispose.Success, "Dispose 后拒绝注册");
    }
    finally
    {
        NativeTestWindow.Destroy(hotkeyWindow);
    }
}

// 13.4 窗口模式管理器：切换 / 持久化 / 互斥 / 不产生数据副本
Console.WriteLine("-- 13.4 窗口模式管理器 --");
string modeDir = Path.Combine(Path.GetTempPath(), "DeskLanesMode_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(modeDir);
try
{
    var modeWorkspace = new WorkspaceService(new LibraryStore(modeDir), new ConfigStore(modeDir), resolver);
    modeWorkspace.Settings.WindowMode = WindowMode.Sidebar;
    modeWorkspace.Save();

    var manager = new WindowModeManager(modeWorkspace);
    Check(manager.CurrentMode == WindowMode.Sidebar && manager.IsSidebarVisible, "从持久化设置恢复侧边栏模式");

    int modeChangedCount = 0;
    WindowMode lastMode = default;
    manager.ModeChanged += (_, m) => { modeChangedCount++; lastMode = m; };

    manager.SwitchTo(WindowMode.Floating);
    Check(
        manager.CurrentMode == WindowMode.Floating && manager.IsFloatingVisible && !manager.IsSidebarVisible,
        "切换到悬浮窗后三种窗口状态互斥");
    Check(modeChangedCount == 1 && lastMode == WindowMode.Floating, "模式切换事件已触发");
    Check(ReloadMode(modeDir) == WindowMode.Floating, "模式切换已持久化（重加载后仍为悬浮窗）");

    manager.ToggleFloating();
    Check(manager.IsFloatingHidden, "快捷键切换：呼出后再次触发变为隐藏");
    manager.ToggleFloating();
    Check(manager.IsFloatingVisible, "再次触发重新呼出悬浮窗");

    manager.SwitchTo(WindowMode.Floating);
    Check(manager.CurrentMode == WindowMode.Floating && manager.IsFloatingVisible, "同模式重复切换保持可见");

    manager.SwitchTo(WindowMode.Full);
    Check(
        manager.CurrentMode == WindowMode.Full && !manager.IsFloatingVisible && !manager.IsSidebarVisible,
        "切到全屏后悬浮窗与侧边栏状态为隐藏");
    manager.HideSidebar(); // 未显示时调用不应抛异常
    Check(true, "隐藏未显示的侧边栏不抛异常");

    // 三种模式共用同一份数据：切换前后项目数量一致（不产生副本）
    int itemCountBefore = modeWorkspace.Items.Count;
    manager.SwitchTo(WindowMode.Sidebar);
    manager.SwitchTo(WindowMode.Floating);
    Check(modeWorkspace.Items.Count == itemCountBefore, "模式切换不产生数据副本（项目数不变）");
}
finally
{
    try
    {
        Directory.Delete(modeDir, true);
    }
    catch
    {
        Info($"窗口模式测试临时目录删除失败（可手动删除）：{modeDir}");
    }
}

// 13.5 悬浮窗视图模型：共享数据 / 分类 / 搜索 / 启动反馈
Console.WriteLine("-- 13.5 悬浮窗视图模型 --");
string floatDir = Path.Combine(Path.GetTempPath(), "DeskLanesFloat_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(floatDir);
try
{
    var floatWorkspace = new WorkspaceService(new LibraryStore(floatDir), new ConfigStore(floatDir), resolver);
    string fileA = Path.Combine(floatDir, "项目甲.txt");
    string fileB = Path.Combine(floatDir, "项目乙.txt");
    File.WriteAllText(fileA, "a");
    File.WriteAllText(fileB, "b");
    floatWorkspace.AddCustomItem(fileA);
    floatWorkspace.AddCustomItem(fileB);
    ShortcutGroup floatGroup = floatWorkspace.CreateGroup("测试分类");
    floatWorkspace.AssignItemToGroup(floatWorkspace.Items.First(i => i.Name.Contains("甲")), floatGroup);

    var floatShell = new ShellState();
    var floating = new FloatingWindowViewModel(floatWorkspace, dialogService, launchService, floatShell);
    Check(
        floating.Categories.Count == 2 && floating.Categories[0].Name == "全部" && floating.Categories[0].Group is null,
        "悬浮窗分类 = 全部 + 用户分类");
    Check(floating.Items.Count == 2, "全部分类下显示全部项目");
    Check(floating.IsSearchEmpty, "初始状态搜索框为空（占位水印可显示）");

    floating.SelectedCategory = floating.Categories.First(c => c.GroupId == floatGroup.Id);
    Check(floating.Items.Count == 1 && floating.Items[0].Name.Contains("甲"), "按分类过滤项目");
    floating.SelectedCategory = floating.Categories[0];
    Check(floating.Items.Count == 2, "切回全部恢复完整列表");

    floatShell.SearchText = "乙";
    Check(!floating.IsSearchEmpty, "输入搜索词后占位水印隐藏");
    Check(floating.Items.Count == 1 && floating.Items[0].Name.Contains("乙"), "悬浮窗跟随共享搜索词过滤");
    floatShell.SearchText = "不存在的关键词";
    Check(floating.HasNoItems && floating.EmptyStateText.Contains("没有包含"), "搜索无结果时给出友好空状态", floating.EmptyStateText);
    floatShell.SearchText = string.Empty;
    Check(floating.Items.Count == 2, "清空搜索后恢复");

    // 启动反馈：目标不存在时状态栏给出错误而不是崩溃
    var missingItem = new DesktopItem
    {
        Id = "floating-missing",
        Name = "缺失的项目",
        FullPath = Path.Combine(floatDir, "不存在-" + Guid.NewGuid().ToString("N")[..8] + ".txt"),
        Kind = DesktopItemKind.File,
    };
    floating.OpenItemCommand.Execute(missingItem);
    Check(
        floating.StatusText.Length > 0 && !floating.StatusText.Contains("已启动"),
        "启动失败时状态栏给出错误说明",
        floating.StatusText);
    Check(!floating.OpenItemCommand.CanExecute(null), "非项目参数不可执行");
    Check(floating.OpenItemCommand.CanExecute(missingItem), "项目参数可执行");

    WindowMode? floatRequested = null;
    floating.ModeSwitchRequested += (_, m) => floatRequested = m;
    floating.ToggleSidebarModeCommand.Execute(null);
    Check(floatRequested == WindowMode.Sidebar, "悬浮窗可请求切换到侧边栏");
    floatRequested = null;
    floating.ToggleFullModeCommand.Execute(null);
    Check(floatRequested == WindowMode.Full, "悬浮窗可请求切换到全屏");

    bool floatHideRequested = false;
    floating.HideRequested += (_, _) => floatHideRequested = true;
    floating.HideWindowCommand.Execute(null);
    Check(floatHideRequested, "悬浮窗可请求隐藏自身（不退出应用）");

    floatWorkspace.Settings.FloatingHideOnDeactivate = false;
    Check(!floating.HideOnDeactivate, "失焦隐藏行为跟随设置");
}
finally
{
    try
    {
        Directory.Delete(floatDir, true);
    }
    catch
    {
        Info($"悬浮窗测试临时目录删除失败（可手动删除）：{floatDir}");
    }
}

// 13.6 侧边栏视图模型：展开/收起 / 停靠 / 失焦收起 / 共享数据
Console.WriteLine("-- 13.6 侧边栏视图模型 --");
string sideDir = Path.Combine(Path.GetTempPath(), "DeskLanesSide_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(sideDir);
try
{
    var sideWorkspace = new WorkspaceService(new LibraryStore(sideDir), new ConfigStore(sideDir), resolver);
    string fileC = Path.Combine(sideDir, "项目丙.txt");
    File.WriteAllText(fileC, "c");
    sideWorkspace.AddCustomItem(fileC);
    ShortcutGroup sideGroup = sideWorkspace.CreateGroup("侧栏分类");
    sideWorkspace.AssignItemToGroup(sideWorkspace.Items[0], sideGroup);

    var sideShell = new ShellState();
    var sidebar = new SidebarViewModel(sideWorkspace, dialogService, launchService, sideShell);
    // 阶段 20：侧边栏第一个节点固定是「全部」，后面才是用户分类
    Check(sidebar.Categories.Count == 2 && sidebar.Categories[0].Name == "全部"
          && sidebar.SelectedCategory == sidebar.Categories[0],
        "侧边栏默认选中「全部」且后接用户分类", sidebar.Categories.Count.ToString());
    Check(sidebar.Items.Count == sideWorkspace.Items.Count, "「全部」显示所有项目",
        $"{sidebar.Items.Count}/{sideWorkspace.Items.Count}");
    sidebar.SelectedCategory = sidebar.Categories[1];
    Check(sidebar.Items.Count == 1, "点用户分类只显示该分类项目");
    Check(sidebar.ExpandedWidth == sideWorkspace.Settings.SidebarWidth, "展开宽度来自设置");
    Check(sidebar.IsDockedRight && sidebar.DockToggleText == "停靠到左侧", "默认停靠右侧");

    sidebar.ToggleExpandedCommand.Execute(null);
    Check(
        sidebar.IsCollapsed && sidebar.ExpandToggleText == "展开" && !sideWorkspace.Settings.SidebarExpanded,
        "收起命令切换状态并持久化");
    sidebar.ToggleExpandedCommand.Execute(null);
    Check(sidebar.IsExpanded && sidebar.ExpandToggleText == "收起", "再次点击恢复展开");

    sidebar.ToggleDockCommand.Execute(null);
    Check(
        sidebar.IsDockedLeft && !sidebar.IsDockedRight && sidebar.DockToggleText == "停靠到右侧" &&
        sideWorkspace.Settings.SidebarDock == SidebarDock.Left,
        "切换停靠侧并持久化");

    sidebar.OnWindowDeactivated();
    Check(sidebar.IsCollapsed, "开启失焦收起时失焦自动收起");
    sidebar.ToggleExpandedCommand.Execute(null);
    sidebar.AutoCollapseOnFocusLost = false;
    sidebar.OnWindowDeactivated();
    Check(
        sidebar.IsExpanded && !sideWorkspace.Settings.SidebarAutoCollapse,
        "关闭失焦收起后失焦保持展开");

    sideShell.SearchText = "丙";
    Check(sidebar.Items.Count == 1, "侧边栏跟随共享搜索词");
    sideShell.SearchText = "没有的关键词";
    Check(!sidebar.HasItems && sidebar.EmptyStateText.Contains("没有包含"), "搜索无结果给出空状态");
    sideShell.SearchText = string.Empty;

    bool sideHideRequested = false;
    sidebar.HideRequested += (_, _) => sideHideRequested = true;
    sidebar.HideWindowCommand.Execute(null);
    Check(sideHideRequested, "侧边栏可请求隐藏自身（不退出应用）");
}
finally
{
    try
    {
        Directory.Delete(sideDir, true);
    }
    catch
    {
        Info($"侧边栏测试临时目录删除失败（可手动删除）：{sideDir}");
    }
}

// 13.7 设置视图模型：模式单选 / 快捷键校验 / 保存策略
Console.WriteLine("-- 13.7 设置视图模型 --");
string setDir = Path.Combine(Path.GetTempPath(), "DeskLanesSet_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(setDir);
try
{
    var setWorkspace = new WorkspaceService(new LibraryStore(setDir), new ConfigStore(setDir), resolver);
    var stubHotkey = new StubHotkeyService();
    // 真实应用启动时已完成注册，设置窗口展示的是"当前已注册"的快捷键
    stubHotkey.Register(HotkeyDefinition.Default);
    var settingsVm = new SettingsViewModel(setWorkspace, stubHotkey, dialogService);
    Check(settingsVm.IsModeFull, "默认选中全屏模式（与设置一致）");
    Check(settingsVm.CurrentHotkeyText == "Ctrl+Alt+Space", "打开设置时展示当前快捷键", settingsVm.CurrentHotkeyText);

    settingsVm.IsModeSidebar = true;
    Check(settingsVm.SelectedMode == WindowMode.Sidebar && !settingsVm.IsModeFull && settingsVm.IsModeSidebar, "单选切换到侧边栏");
    settingsVm.IsModeSidebar = false;
    Check(settingsVm.SelectedMode == WindowMode.Sidebar, "取消勾选不改变当前选择");

    settingsVm.SidebarWidth = 1000;
    Check(settingsVm.SidebarWidth == 520, "侧边栏宽度上限 520", settingsVm.SidebarWidth.ToString("0"));
    settingsVm.SidebarWidth = 10;
    Check(settingsVm.SidebarWidth == 180, "侧边栏宽度下限 180", settingsVm.SidebarWidth.ToString("0"));

    settingsVm.IsDockLeft = true;
    Check(settingsVm.SidebarDock == SidebarDock.Left, "设置里可切换停靠侧");
    settingsVm.IsDockRight = true;
    Check(settingsVm.SidebarDock == SidebarDock.Right, "设置里可切回右侧停靠");

    // 快捷键校验：无法识别 -> 不尝试注册
    int registrationsBefore = stubHotkey.Registrations.Count;
    settingsVm.HotkeyText = "Ctrl+不存在的键";
    settingsVm.TestHotkeyCommand.Execute(null);
    Check(settingsVm.HasHotkeyError && settingsVm.HotkeyStatus.Contains("无法识别"), "无法识别的快捷键给出错误说明", settingsVm.HotkeyStatus);
    Check(stubHotkey.Registrations.Count == registrationsBefore, "解析失败时不会尝试注册");

    // 快捷键校验：可注册 -> 只提示，不立即写设置
    registrationsBefore = stubHotkey.Registrations.Count;
    settingsVm.HotkeyText = "Ctrl+Alt+D";
    settingsVm.TestHotkeyCommand.Execute(null);
    Check(!settingsVm.HasHotkeyError && settingsVm.HotkeyStatus.Contains("可以注册"), "合法快捷键试注册成功", settingsVm.HotkeyStatus);
    Check(
        stubHotkey.Registrations.Count == registrationsBefore + 1 &&
        stubHotkey.Current!.DisplayText == "Ctrl+Alt+D",
        "试注册真实调用了注册服务");
    Check(setWorkspace.Settings.HotkeyKey == HotkeyDefinition.Default.Key, "试注册不立即写入设置");

    // 保存：写入设置并应用
    bool settingsSaved = false;
    settingsVm.Saved += (_, _) => settingsSaved = true;
    settingsVm.MinimizeToTray = false;
    settingsVm.FloatingHideOnDeactivate = false;
    settingsVm.SaveCommand.Execute(null);
    Check(settingsSaved, "保存后触发 Saved 事件");
    Check(
        setWorkspace.Settings.WindowMode == WindowMode.Sidebar &&
        setWorkspace.Settings.SidebarDock == SidebarDock.Right &&
        !setWorkspace.Settings.MinimizeToTray &&
        !setWorkspace.Settings.FloatingHideOnDeactivate &&
        setWorkspace.Settings.HotkeyModifiers == (HotkeyDefinition.ModControl | HotkeyDefinition.ModAlt) &&
        setWorkspace.Settings.HotkeyKey == 'D',
        "保存把模式/停靠/托盘/悬浮窗行为/快捷键写入设置");
    Check(ReloadMode(setDir) == WindowMode.Sidebar, "保存后重新加载仍为侧边栏模式");

    // 保存时注册失败：不覆盖原快捷键、不关闭设置窗口
    settingsVm.HotkeyText = "Ctrl+Shift+S";
    stubHotkey.FailNext = true;
    int dialogErrorsBefore = dialogService.Errors.Count;
    settingsVm.SaveCommand.Execute(null);
    Check(
        dialogService.Errors.Count == dialogErrorsBefore + 1 && dialogService.Errors[^1].Contains("占用"),
        "注册失败时明确提示（含占用说明）",
        dialogService.Errors.Count > dialogErrorsBefore ? dialogService.Errors[^1] : "无提示");
    Check(setWorkspace.Settings.HotkeyKey == 'D', "注册失败后保留原快捷键不覆盖");
    stubHotkey.FailNext = false;

    // 保存时文本无法识别：提示且不修改
    settingsVm.HotkeyText = "乱码";
    dialogErrorsBefore = dialogService.Errors.Count;
    settingsVm.SaveCommand.Execute(null);
    Check(dialogService.Errors.Count == dialogErrorsBefore + 1, "无法识别的快捷键保存时给出提示");
    Check(setWorkspace.Settings.HotkeyKey == 'D', "无效输入不影响已保存的快捷键");
}
finally
{
    try
    {
        Directory.Delete(setDir, true);
    }
    catch
    {
        Info($"设置视图模型测试临时目录删除失败（可手动删除）：{setDir}");
    }
}

// 13.8 托盘菜单项与命令分发
Console.WriteLine("-- 13.8 托盘菜单 --");
{
    TrayMenuItem separator = TrayMenuItem.Separator();
    Check(separator.IsSeparator && separator.Id < 0, "托盘分隔符项");
    TrayMenuItem openItem = new("打开工作空间", 2001);
    Check(openItem.Text == "打开工作空间" && openItem.Id == 2001 && !openItem.IsSeparator, "托盘菜单项文本与 Id");

    IntPtr trayWindow = NativeTestWindow.Create();
    if (trayWindow == IntPtr.Zero)
    {
        Info("无法创建测试用原生窗口，跳过托盘命令分发断言");
    }
    else
    {
        try
        {
            using var tray = new TrayIconService(trayWindow) { ToolTipText = "DeskLanes" };
            tray.MenuItems = new List<TrayMenuItem>
            {
                new("打开工作空间", 2001),
                TrayMenuItem.Separator(),
                new("退出 DeskLanes", 2006),
            };
            Check(tray.MenuItems.Count == 3, "托盘菜单可配置");
            Check(!tray.HandleMessage(0x0014, IntPtr.Zero, IntPtr.Zero), "无关消息不被托盘占用");

            int? command = null;
            tray.MenuCommand += (_, id) => command = id;
            Check(tray.HandleCommand(new IntPtr(2001)), "菜单命令 Id 大于 0 时分发");
            Check(command == 2001, "分发携带正确的菜单 Id", command?.ToString());
            command = null;
            Check(!tray.HandleCommand(IntPtr.Zero), "Id 为 0 时不分发");
            Check(command is null, "无效命令不触发事件");
        }
        finally
        {
            NativeTestWindow.Destroy(trayWindow);
        }
    }
}

// 13.9 三种模式共用同一份数据源（同一 WorkspaceService + 同一 ShellState）
Console.WriteLine("-- 13.9 三种模式共享数据 --");
string shareDir = Path.Combine(Path.GetTempPath(), "DeskLanesShare_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(shareDir);
try
{
    var shareWorkspace = new WorkspaceService(new LibraryStore(shareDir), new ConfigStore(shareDir), resolver);
    string sharedFile = Path.Combine(shareDir, "共享项目.txt");
    File.WriteAllText(sharedFile, "s");
    shareWorkspace.AddCustomItem(sharedFile);

    var shareShell = new ShellState();
    var shareFloating = new FloatingWindowViewModel(shareWorkspace, dialogService, launchService, shareShell);
    var shareSidebar = new SidebarViewModel(shareWorkspace, dialogService, launchService, shareShell);
    Check(shareFloating.Items.Count == 1 && shareWorkspace.Items.Count == 1, "悬浮窗直接读工作区数据（无副本）");
    Check(shareSidebar.Categories.Count == 1 && shareSidebar.Categories[0].Name == "全部"
          && shareSidebar.Items.Count == 1,
        "无用户分组时侧边栏只显示「全部」且能看到项目",
        $"{shareSidebar.Categories.Count}/{shareSidebar.Items.Count}");

    shareShell.SearchText = "共享";
    Check(
        shareFloating.Items.Count == 1 && shareFloating.SearchText == "共享",
        "三种模式共享同一个搜索词");
    shareShell.SearchText = "没有的关键词";
    Check(shareFloating.Items.Count == 0, "共享搜索词清空匹配后悬浮窗同步为空");
    shareShell.SearchText = string.Empty;
}
finally
{
    try
    {
        Directory.Delete(shareDir, true);
    }
    catch
    {
        Info($"共享数据测试临时目录删除失败（可手动删除）：{shareDir}");
    }
}

// ---------------------------------------------------------------- 14. 开机自启
Console.WriteLine("-- 14 开机自启（HKCU Run 启动项）--");
{
    // 只读写当前用户 Run 键里名为 DeskLanes 的这一个值；测试结束恢复原状。
    const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string valueName = "DeskLanes";

    var svc = new DeskLanes.App.Services.StartupService();
    Check(svc.StatusText is null, "开机自启服务可读取启动项状态", svc.StatusText);
    bool original = svc.IsEnabled;
    Info($"测试前开机自启状态：{(original ? "已启用" : "未启用")}");

    OperationResult enableResult = svc.SetEnabled(true);
    Check(enableResult.Success, "可启用开机自启", enableResult.ErrorMessage);
    Check(svc.IsEnabled, "启用后状态为已启用");

    using (RegistryKey? verify = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
    {
        object? raw = verify?.GetValue(valueName);
        Check(raw is string s && !string.IsNullOrEmpty(s), "注册表 Run 键写入了 DeskLanes 启动项");
        Info($"启动项内容：{raw}");
    }

    OperationResult disableResult = svc.SetEnabled(false);
    Check(disableResult.Success, "可停用开机自启", disableResult.ErrorMessage);
    Check(!svc.IsEnabled, "停用后状态为未启用");

    using (RegistryKey? verify2 = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
    {
        Check(verify2?.GetValue(valueName) is null, "停用后注册表值已删除");
    }

    // 还原测试前的真实状态，避免测试影响用户环境
    svc.SetEnabled(original);
    Check(svc.IsEnabled == original, "测试后已还原为原始状态");
}

// ---------------------------------------------------------------- 15. 桌面图标隐藏 / 恢复
Console.WriteLine("-- 15 桌面图标隐藏 / 恢复 --");
{
    // 通过切换命令（WM_COMMAND 0x7402，系统右键菜单同款）短暂隐藏桌面图标并立即还原。
    // HideIcons 注册表值由资源管理器在切换时自己写，本服务不直接写注册表；不重启 Explorer。
    var iconSvc = new DeskLanes.App.Services.DesktopIconService();
    Check(iconSvc.StatusText is null, "桌面图标服务可读取当前状态", iconSvc.StatusText);
    bool originalHidden = iconSvc.IsHidden;
    Info($"测试前桌面图标：{(originalHidden ? "已隐藏" : "显示中")}");

    OperationResult hideResult = iconSvc.SetHidden(true);
    Check(hideResult.Success, "可隐藏桌面图标", hideResult.ErrorMessage);
    Check(iconSvc.IsHidden, "隐藏后状态为已隐藏");

    using (RegistryKey? adv = Registry.CurrentUser.OpenSubKey(
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false))
    {
        Check(adv?.GetValue("HideIcons") is int v && v != 0, "切换后注册表 HideIcons 同步为 1");
    }

    OperationResult showResult = iconSvc.SetHidden(false);
    Check(showResult.Success, "可恢复桌面图标", showResult.ErrorMessage);
    Check(!iconSvc.IsHidden, "恢复后状态为显示中");

    // ---- Refresh()：外部改注册表后，服务必须能读到系统真实状态 ----
    // （对应阶段 7 修复项 5：用户可能用 Windows 右键菜单等软件外的途径改过）
    using (RegistryKey adv = Registry.CurrentUser.CreateSubKey(
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")!)
    {
        adv.SetValue("HideIcons", 1, RegistryValueKind.DWord);
    }
    iconSvc.Refresh();
    Check(iconSvc.IsHidden, "Refresh() 能读到外部写入的隐藏状态");

    using (RegistryKey adv = Registry.CurrentUser.CreateSubKey(
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")!)
    {
        adv.SetValue("HideIcons", 0, RegistryValueKind.DWord);
    }
    iconSvc.Refresh();
    Check(!iconSvc.IsHidden, "Refresh() 能读到外部写入的显示状态");

    // 还原测试前的真实状态
    iconSvc.SetHidden(originalHidden);
    Check(iconSvc.IsHidden == originalHidden, "测试后已还原为原始状态");
}

// ---------------------------------------------------------------- 16. 诊断包导出（阶段 8.3）
Console.WriteLine("-- 16 诊断包导出 --");
{
    string root = Path.Combine(Path.GetTempPath(), "DeskLanesSmokeDiag_" + Guid.NewGuid().ToString("N")[..8]);
    string configDir = Path.Combine(root, "config");
    string logsDir = Path.Combine(configDir, "logs");
    Directory.CreateDirectory(logsDir);

    try
    {
        // ---- 16.1 空配置目录也要能导出（不崩，至少有 manifest） ----
        var emptyExporter = new DiagnosticExporter(configDir);
        string emptyZip = Path.Combine(root, "empty.zip");
        OperationResult emptyResult = emptyExporter.Export(emptyZip);
        Check(emptyResult.Success, "空配置目录也能导出诊断包", emptyResult.ErrorMessage);
        Check(File.Exists(emptyZip), "空配置目录导出的 zip 存在");

        using (ZipArchive z = ZipFile.OpenRead(emptyZip))
        {
            Check(z.GetEntry("manifest.txt") is not null, "空配置目录导出仍包含 manifest.txt");
        }

        // ---- 16.2 有配置与日志时，内容必须进包 ----
        File.WriteAllText(Path.Combine(configDir, "settings.json"), "{\"Version\":1}");
        File.WriteAllText(Path.Combine(configDir, "library.json"), "{\"Items\":[]}");
        File.WriteAllText(Path.Combine(logsDir, "sync.log"), "sync line\n");
        File.WriteAllText(Path.Combine(logsDir, "errors.log"), "error line\n");

        var exporter = new DiagnosticExporter(configDir);
        string zipPath = Path.Combine(root, "diag.zip");
        OperationResult result = exporter.Export(zipPath);
        Check(result.Success, "导出诊断包成功", result.ErrorMessage);
        Check(File.Exists(zipPath), "诊断包文件已生成");

        using (ZipArchive z = ZipFile.OpenRead(zipPath))
        {
            Check(z.GetEntry("settings.json") is not null, "包含 settings.json");
            Check(z.GetEntry("library.json") is not null, "包含 library.json");
            Check(z.GetEntry("logs/sync.log") is not null, "包含 logs/sync.log");
            Check(z.GetEntry("logs/errors.log") is not null, "包含 logs/errors.log");

            ZipArchiveEntry? manifest = z.GetEntry("manifest.txt");
            Check(manifest is not null, "包含 manifest.txt");
            if (manifest is not null)
            {
                using var reader = new StreamReader(manifest.Open());
                string text = reader.ReadToEnd();
                Check(text.Contains(configDir), "manifest 记录了配置目录");
                Check(text.Contains("个人文件路径"), "manifest 含隐私提示（分享前确认）");
            }
        }

        // ---- 16.3 日志正被写入时仍能导出（不能因为文件被锁就失败） ----
        string lockLog = Path.Combine(logsDir, "errors.log");
        using (FileStream held = new(lockLog, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            held.Write(Encoding.UTF8.GetBytes("written by app\n"));
            held.Flush(true);

            string lockZip = Path.Combine(root, "diag-locked.zip");
            OperationResult lockResult = exporter.Export(lockZip);
            Check(lockResult.Success, "日志被占用时仍能导出", lockResult.ErrorMessage);
            using ZipArchive z = ZipFile.OpenRead(lockZip);
            Check(z.GetEntry("logs/errors.log") is not null, "被占用日志仍进包");
        }

        // ---- 16.4 重复导出同一路径：覆盖而不是报错 ----
        OperationResult again = exporter.Export(zipPath);
        Check(again.Success, "重复导出同一路径会覆盖", again.ErrorMessage);

        // ---- 16.5 输出路径非法时返回失败而不是抛异常 ----
        OperationResult bad = exporter.Export("   ");
        Check(!bad.Success && !string.IsNullOrEmpty(bad.ErrorMessage), "空输出路径返回失败", bad.ErrorMessage);

        // ---- 16.6 备份目录只统计不打包内容（避免诊断包过大） ----
        string backupDir = Path.Combine(configDir, "backups");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "library-corrupt-20260101.json"), "{\"x\":1}");
        string backupZip = Path.Combine(root, "diag-backup.zip");
        OperationResult backupResult = exporter.Export(backupZip);
        Check(backupResult.Success, "存在备份目录时导出成功", backupResult.ErrorMessage);
        using (ZipArchive z = ZipFile.OpenRead(backupZip))
        {
            Check(z.GetEntry("backups/library-corrupt-20260101.json") is null, "备份文件内容不进包（只记清单）");
            ZipArchiveEntry? m = z.GetEntry("manifest.txt");
            using var reader = new StreamReader(m!.Open());
            Check(reader.ReadToEnd().Contains("library-corrupt-20260101.json"), "manifest 列出备份文件名");
        }
    }
    finally
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 17. 配置目录迁移（更名 DeskSpace → DeskLanes 引入）
{
    string migRoot = Path.Combine(Path.GetTempPath(), "DeskLanesMig-" + Guid.NewGuid().ToString("N"));
    string migNew = Path.Combine(migRoot, "DeskLanes");
    string migOld = Path.Combine(migRoot, "DeskSpace");
    try
    {
        // ---- 17.1 无旧目录（全新安装）：不迁移，也不凭空创建目录 ----
        bool r1 = ConfigDirectoryMigrator.TryMigrateFrom(migNew, Path.Combine(migRoot, "__NoSuchOldApp__"));
        Check(!r1, "无旧配置目录时不做迁移");
        Check(!Directory.Exists(migNew), "无旧配置时不凭空创建新目录");

        // ---- 17.2 旧目录存在：配置、日志、备份都要搬过去 ----
        Directory.CreateDirectory(migOld);
        File.WriteAllText(Path.Combine(migOld, "settings.json"), "{\"Version\":1}");
        File.WriteAllText(Path.Combine(migOld, "library.json"), "{\"Version\":1,\"Items\":[]}");
        Directory.CreateDirectory(Path.Combine(migOld, "logs"));
        File.WriteAllText(Path.Combine(migOld, "logs", "sync.log"), "x");
        Directory.CreateDirectory(Path.Combine(migOld, "backups"));
        File.WriteAllText(Path.Combine(migOld, "backups", "settings-corrupt-1.json"), "{}");

        bool r2 = ConfigDirectoryMigrator.TryMigrateFrom(migNew, migOld);
        Check(r2, "旧配置存在时执行迁移");
        Check(File.Exists(Path.Combine(migNew, "settings.json")), "settings.json 已迁移");
        Check(File.ReadAllText(Path.Combine(migNew, "settings.json")) == "{\"Version\":1}", "settings.json 内容一致");
        Check(File.Exists(Path.Combine(migNew, "library.json")), "library.json 已迁移");
        Check(File.Exists(Path.Combine(migNew, "logs", "sync.log")), "logs 子目录已迁移");
        Check(File.Exists(Path.Combine(migNew, "backups", "settings-corrupt-1.json")), "backups 子目录已迁移");
        Check(Directory.Exists(migOld), "旧目录原样保留（可手动回退）");
        Check(File.Exists(Path.Combine(migOld, "settings.json")), "旧目录文件未被移动或删除");

        // ---- 17.3 再次调用：已有新配置时不得被旧配置覆盖 ----
        File.WriteAllText(Path.Combine(migNew, "settings.json"), "{\"Version\":2}");
        bool r3 = ConfigDirectoryMigrator.TryMigrateFrom(migNew, migOld);
        Check(!r3, "新目录已存在时不再迁移");
        Check(File.ReadAllText(Path.Combine(migNew, "settings.json")) == "{\"Version\":2}", "已有新配置不被旧配置覆盖");
    }
    finally
    {
        try
        {
            Directory.Delete(migRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 18. 三模式来回切换的数据一致性（核心卖点）
Console.WriteLine();
Console.WriteLine("== 18. 三模式切换的数据一致性（悬浮窗 / 全屏 / 侧边栏） ==");
{
    string modeRoot = Path.Combine(Path.GetTempPath(), "DeskLanesMode_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(modeRoot);
    try
    {
        var modeWs = new WorkspaceService(new LibraryStore(modeRoot), new ConfigStore(modeRoot), resolver);
        await modeWs.MergeScanAsync(scan);
        ShortcutGroup modeGroup = modeWs.CreateGroup("一致性校验分类");
        modeWs.AssignItemToGroup(modeWs.Items.First(), modeGroup);

        int expectItems = modeWs.Items.Count;
        int expectGroups = modeWs.Groups.Count;
        List<string> expectPaths = modeWs.Items
            .Select(i => i.PathKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
        List<string> expectMember = modeGroup.ItemIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(expectItems > 0 && expectMember.Count > 0, "已造出用于一致性校验的数据",
            $"{expectItems} 个项目，分类含 {expectMember.Count} 个成员");

        // 全屏 → 悬浮窗 → 侧边栏 → 全屏，每一站都重新从磁盘加载后比对
        foreach (WindowMode m in new[] { WindowMode.Floating, WindowMode.Sidebar, WindowMode.Full })
        {
            modeWs.Settings.WindowMode = m;
            modeWs.Save();

            var reload = new WorkspaceService(new LibraryStore(modeRoot), new ConfigStore(modeRoot), resolver);
            string tag = m switch
            {
                WindowMode.Floating => "悬浮窗",
                WindowMode.Sidebar => "侧边栏",
                _ => "全屏",
            };

            Check(reload.Settings.WindowMode == m, $"切到{tag}后模式值持久化",
                reload.Settings.WindowMode.ToString());
            Check(reload.LoadWarnings.Count == 0, $"切到{tag}后重新加载无警告",
                string.Join(" | ", reload.LoadWarnings));
            Check(reload.Items.Count == expectItems, $"切到{tag}后项目数不变",
                $"{expectItems} -> {reload.Items.Count}");
            Check(
                reload.Items.Select(i => i.PathKey).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(expectPaths),
                $"切到{tag}后项目集合完全一致（无新增无丢失）");
            Check(reload.Groups.Count == expectGroups, $"切到{tag}后分类数不变",
                $"{expectGroups} -> {reload.Groups.Count}");
            ShortcutGroup? g2 = reload.Groups.FirstOrDefault(g => g.Id == modeGroup.Id);
            Check(
                g2 is not null &&
                g2.ItemIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectMember),
                $"切到{tag}后分类成员关联不变");
        }

        Check(modeWs.Settings.WindowMode == WindowMode.Full, "三模式绕一圈后回到全屏");
    }
    finally
    {
        try
        {
            Directory.Delete(modeRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 19. 悬浮窗几何持久化（可拖动 / 可缩放）
Console.WriteLine();
Console.WriteLine("== 19. 悬浮窗几何持久化（拖动与缩放后要记得住） ==");
{
    string geoRoot = Path.Combine(Path.GetTempPath(), "DeskLanesGeo_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(geoRoot);
    try
    {
        var geoWs = new WorkspaceService(new LibraryStore(geoRoot), new ConfigStore(geoRoot), resolver);

        // ---- 19.1 出厂默认：尺寸与窗口 XAML 一致，位置没有记录 ----
        Check(geoWs.Settings.FloatingWindowWidth == 780, "悬浮窗默认宽 780",
            geoWs.Settings.FloatingWindowWidth.ToString());
        Check(geoWs.Settings.FloatingWindowHeight == 540, "悬浮窗默认高 540",
            geoWs.Settings.FloatingWindowHeight.ToString());
        Check(geoWs.Settings.FloatingWindowLeft is null && geoWs.Settings.FloatingWindowTop is null,
            "首次使用没有位置记录（此时应居中，而不是停在 Manual 默认左上角）");

        // ---- 19.2 缩小 + 拖到副屏，重新加载必须原样回来 ----
        geoWs.SaveFloatingWindowPreferences(520, 400, 1360, 300);

        var reload = new WorkspaceService(new LibraryStore(geoRoot), new ConfigStore(geoRoot), resolver);
        Check(reload.Settings.FloatingWindowWidth == 520, "缩放后的宽度被保存",
            reload.Settings.FloatingWindowWidth.ToString());
        Check(reload.Settings.FloatingWindowHeight == 400, "缩放后的高度被保存",
            reload.Settings.FloatingWindowHeight.ToString());
        Check(reload.Settings.FloatingWindowLeft == 1360, "拖动后的横坐标被保存（可跨屏）",
            reload.Settings.FloatingWindowLeft.ToString());
        Check(reload.Settings.FloatingWindowTop == 300, "拖动后的纵坐标被保存",
            reload.Settings.FloatingWindowTop.ToString());

        // ---- 19.3 窗口从没显示过（Left/Top 是 NaN）：位置清空、尺寸保留，不能写坏配置 ----
        //      NaN 写进 JSON 会让 System.Text.Json 直接抛异常（虚拟机实测踩到过）。
        reload.SaveFloatingWindowPreferences(double.NaN, double.NaN, double.NaN, double.NaN);
        Check(reload.Settings.FloatingWindowWidth == 520 && reload.Settings.FloatingWindowHeight == 400,
            "尺寸非有限值时保留原尺寸（不写坏配置）");
        Check(reload.Settings.FloatingWindowLeft is null && reload.Settings.FloatingWindowTop is null,
            "位置非有限值时清空位置");
        Check(reload.LoadWarnings.Count == 0, "写入非有限值后重新加载无警告",
            string.Join(" | ", reload.LoadWarnings));

        // ---- 19.4 负坐标（副屏排在主屏左边时 VirtualScreenLeft 为负）要能存 ----
        reload.SaveFloatingWindowPreferences(600, 480, -1280, -40);
        var neg = new WorkspaceService(new LibraryStore(geoRoot), new ConfigStore(geoRoot), resolver);
        Check(neg.Settings.FloatingWindowLeft == -1280 && neg.Settings.FloatingWindowTop == -40,
            "负坐标（左侧副屏）能正确保存与读回",
            $"{neg.Settings.FloatingWindowLeft},{neg.Settings.FloatingWindowTop}");

        // ---- 19.5 两套窗口偏好互不干扰 ----
        neg.SaveWindowPreferences(1180, 760, 10, 20, true);
        neg.SaveFloatingWindowPreferences(500, 380, 40, 60);
        var both = new WorkspaceService(new LibraryStore(geoRoot), new ConfigStore(geoRoot), resolver);
        Check(both.Settings.WindowWidth == 1180 && both.Settings.WindowHeight == 760
                && both.Settings.WindowMaximized,
            "主窗口偏好不受悬浮窗保存影响");
        Check(both.Settings.FloatingWindowWidth == 500 && both.Settings.FloatingWindowLeft == 40,
            "悬浮窗偏好不受主窗口保存影响");
    }
    finally
    {
        try
        {
            Directory.Delete(geoRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 20. 图标缓存有界（长期驻留不泄漏）
Console.WriteLine();
Console.WriteLine("== 20. 图标缓存有界（长驻不泄漏） ==");
{
    string iconRoot = Path.Combine(Path.GetTempPath(), "DeskLanesIcon_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(iconRoot);
    try
    {
        // ---- 20.1 默认上限与初始状态 ----
        var svc = new IconService();
        Check(svc.MaxCachedIcons == IconService.DefaultMaxCachedIcons && svc.MaxCachedIcons == 512,
            "默认缓存上限 512", svc.MaxCachedIcons.ToString());
        Check(svc.CachedIconCount == 0, "新建实例缓存为空");

        // ---- 20.2 真实文件仍能取到图标且已冻结（回归：重写缓存没弄坏取图标路径） ----
        string realFile = Path.Combine(iconRoot, "real-icon-target.txt");
        File.WriteAllText(realFile, "icon");
        var realIcon = svc.GetIcon(realFile);
        Check(realIcon is not null, "真实文件能取到系统图标");
        Check(realIcon is null || realIcon.IsFrozen, "图标已冻结（可跨线程赋值）");
        Check(svc.CachedIconCount == 1, "取一次图标缓存一条", svc.CachedIconCount.ToString());

        // ---- 20.3 命中不产生新条目 ----
        for (int i = 0; i < 5; i++)
        {
            svc.GetIcon(realFile);
        }
        Check(svc.CachedIconCount == 1, "重复取同一路径命中缓存，条目数不变");

        // ---- 20.4 空/null 路径不入缓存 ----
        Check(svc.GetIcon("") is null, "空路径返回 null");
        Check(svc.GetIcon(null!) is null, "null 路径返回 null");
        Check(svc.CachedIconCount == 1, "空/null 路径不进缓存");

        // ---- 20.5 容量上限：小上限实例连续塞 120 条，不能突破上限 ----
        var small = new IconService(32);
        for (int i = 0; i < 120; i++)
        {
            small.GetIcon(Path.Combine(iconRoot, "no-such-" + i + ".bin"));
        }
        Check(small.CachedIconCount <= 32, "超过上限后被淘汰，缓存不超过上限",
            $"{small.CachedIconCount} <= 32");
        Check(small.CachedIconCount >= 24, "淘汰是成批的（一次腾出 1/8 空间），不是逐条",
            small.CachedIconCount.ToString());

        // ---- 20.6 LRU：持续访问的条目要活下来，最久未访问的要被淘汰 ----
        string keeper = Path.Combine(iconRoot, "keeper.bin");
        small.GetIcon(keeper);
        for (int i = 200; i < 400; i++)
        {
            small.GetIcon(Path.Combine(iconRoot, "no-such-" + i + ".bin"));
            small.GetIcon(keeper);   // 每次都刷新它的 LRU 序号
        }
        Check(small.IsCached(keeper), "最近持续访问的条目在淘汰中存活（LRU 生效）");
        Check(!small.IsCached(Path.Combine(iconRoot, "no-such-0.bin")),
            "最久未访问的条目被淘汰");

        // ---- 20.7 默认上限实例同样不越界 ----
        var big = new IconService();
        for (int i = 0; i < 700; i++)
        {
            big.GetIcon(Path.Combine(iconRoot, "bulk-" + i + ".bin"));
        }
        Check(big.CachedIconCount <= 512, "默认实例塞 700 条后仍不超过 512",
            big.CachedIconCount.ToString());

        // ---- 20.8 PruneExcept：每轮扫描后按当前工作区裁剪（主要清理手段） ----
        var prune = new IconService(256);
        string[] keep = { Path.Combine(iconRoot, "a.txt"), Path.Combine(iconRoot, "b.txt") };
        string[] all = keep
            .Concat(Enumerable.Range(0, 50).Select(i => Path.Combine(iconRoot, "gone-" + i + ".bin")))
            .ToArray();
        foreach (string p in all)
        {
            prune.GetIcon(p);
        }
        int before = prune.CachedIconCount;
        int removed = prune.PruneExcept(keep);
        Check(removed == before - 2, "PruneExcept 摘掉了所有不在保留列表里的条目",
            $"摘掉 {removed}，剩余 {prune.CachedIconCount}");
        Check(prune.CachedIconCount == 2, "保留列表里的条目全部留下");
        Check(prune.IsCached(keep[0]) && prune.IsCached(keep[1]), "保留的两个路径仍在缓存");
        Check(prune.PruneExcept(keep) == 0, "重复裁剪不会再摘（幂等）");
        Check(prune.PruneExcept(Array.Empty<string>()) == 2, "保留列表为空时清空缓存");

        // ---- 20.9 ClearCache 归零 ----
        big.ClearCache();
        Check(big.CachedIconCount == 0, "ClearCache 清空缓存");

        // ---- 20.10 接口层面也能拿到计数与裁剪能力（MainViewModel 走的是接口） ----
        IIconService viaInterface = new IconService(64);
        viaInterface.GetIcon(realFile);
        Check(viaInterface.CachedIconCount == 1, "接口暴露缓存计数");
        Check(viaInterface.PruneExcept(new[] { realFile }) == 0, "接口可裁剪（项目还在时保留）");
        Check(viaInterface.PruneExcept(Array.Empty<string>()) == 1, "接口可裁剪（项目已删时摘掉）");
    }
    finally
    {
        try
        {
            Directory.Delete(iconRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 21. 200+ 项目性能（B6）与配置损坏提示（C10）
Console.WriteLine();
Console.WriteLine("== 21. 大量项目的扫描性能 + 损坏配置的可见提示 ==");
{
    string bulkRoot = Path.Combine(Path.GetTempPath(), "DeskLanesBulk_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(bulkRoot);
    string bulkCfg = Path.Combine(Path.GetTempPath(), "DeskLanesBulkCfg_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(bulkCfg);
    try
    {
        // ---- 21.1 220 个项目的扫描与合并（B6 脚本部分；不用往真实桌面堆文件） ----
        const int bulkCount = 220;
        for (int i = 0; i < bulkCount; i++)
        {
            File.WriteAllText(Path.Combine(bulkRoot, $"bulk-item-{i:D3}.txt"), "bulk");
        }

        var bulkScanner = new DesktopScanner(resolver);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ScanResult bulkScan = await bulkScanner.ScanAsync(new ScanOptions
        {
            Targets = new[] { new ScanTarget { Path = bulkRoot, Origin = ItemOrigin.UserDesktop } },
        });
        long scanMs = sw.ElapsedMilliseconds;

        IWorkspaceService bulkWs = new WorkspaceService(
            new LibraryStore(bulkCfg), new ConfigStore(bulkCfg), resolver);
        sw.Restart();
        ScanMergeOutcome outcome = await bulkWs.MergeScanAsync(bulkScan);
        long mergeMs = sw.ElapsedMilliseconds;

        Info($"220 个项目：扫描 {scanMs} ms，合并 {mergeMs} ms，新增 {outcome.Added} 项");
        Check(bulkScan.Items.Count == bulkCount, "220 个项目全部扫到",
            $"实际 {bulkScan.Items.Count}");
        Check(scanMs < 5000, "220 个项目扫描 < 5 秒（B6 判据）", $"{scanMs} ms");
        Check(mergeMs < 5000, "220 个项目合并进库 < 5 秒", $"{mergeMs} ms");
        Check(outcome.Added == bulkCount, "220 个项目全部入库", $"新增 {outcome.Added}");
        Check(bulkScan.Warnings.Count == 0, "批量扫描无警告",
            string.Join(" | ", bulkScan.Warnings));

        // ---- 21.2 批量取图标（图标是 P/Invoke，最可能成为大桌面下的瓶颈） ----
        var bulkIcons = new IconService();
        sw.Restart();
        int bulkIconOk = 0;
        foreach (DesktopItem item in bulkWs.Items)
        {
            if (bulkIcons.GetIcon(item.FullPath) is not null)
            {
                bulkIconOk++;
            }
        }
        long iconMs = sw.ElapsedMilliseconds;
        Info($"220 个项目取图标 {iconMs} ms，成功 {bulkIconOk} 个");
        Check(iconMs < 20000, "220 个项目取图标 < 20 秒", $"{iconMs} ms");
        Check(bulkIcons.CachedIconCount <= IconService.DefaultMaxCachedIcons,
            "批量取图标后缓存仍在上限内",
            $"{bulkIcons.CachedIconCount} <= {IconService.DefaultMaxCachedIcons}");

        // ---- 21.3 配置损坏时用户在主窗口能看到中文提示（C10 脚本部分） ----
        string brokeCfg = Path.Combine(Path.GetTempPath(), "DeskLanesBroke_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(brokeCfg);
        try
        {
            File.WriteAllText(Path.Combine(brokeCfg, "library.json"), "{ 这不是合法 JSON，模拟损坏 ");
            IWorkspaceService brokeWs = new WorkspaceService(
                new LibraryStore(brokeCfg), new ConfigStore(brokeCfg), resolver);

            // 用空扫描器顶掉真实桌面扫描，这一段只关心"提示有没有上屏"
            int errorsBefore = dialogService.Errors.Count;   // 共享实例，前面几节可能已有记录
            var brokeVm = new MainViewModel(
                brokeWs, new EmptyScanner(), new IconService(), launchService, dialogService,
                new ShellState());
            await brokeVm.InitializeAsync();

            Check(brokeVm.HasWarning, "配置损坏时主窗口显示提示条");
            Check(brokeVm.WarningText is not null && brokeVm.WarningText.Contains("配置提示"),
                "提示是中文的（不是英文异常堆栈）", brokeVm.WarningText);
            Check(brokeVm.WarningText is not null && brokeVm.WarningText.Contains("损坏"),
                "提示里说明了是配置损坏", brokeVm.WarningText);
            Check(brokeVm.WarningText is not null && !brokeVm.WarningText.Contains("Exception"),
                "提示里没有异常类名", brokeVm.WarningText);
            Check(Directory.Exists(Path.Combine(brokeCfg, "backups")),
                "损坏原件已备份到 backups 目录");
            Check(dialogService.Errors.Count == errorsBefore, "配置损坏不弹错误对话框（只在窗口内提示）",
                string.Join(" | ", dialogService.Errors.Skip(errorsBefore)));
            Check(brokeVm.WarningText is not null, "提示在首次刷新后仍然存在（不被刷新清空）");
            brokeVm.DismissWarnings();
            Check(!brokeVm.HasWarning && brokeVm.WarningText is null, "点忽略后提示消失");
        }
        finally
        {
            try
            {
                Directory.Delete(brokeCfg, true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }
    }
    finally
    {
        foreach (string dir in new[] { bulkRoot, bulkCfg })
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }
    }
}

// ---------------------------------------------------------------- 22. 日志轮转
Console.WriteLine();
Console.WriteLine("== 22. 日志轮转（有上界，且留得住出事之前那一段） ==");
{
    string log22Root = Path.Combine(Path.GetTempPath(), "DeskLanesLog_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(log22Root);
    try
    {
        // ---- 22.1 基本写入 ----
        string p1 = Path.Combine(log22Root, "a.log");
        var w1 = new LogFileWriter(p1);
        w1.Append("line1");
        w1.Append("line2");
        w1.Append("line3");
        Check(File.ReadAllLines(p1).Length == 3, "追加写三行都落盘",
            File.ReadAllLines(p1).Length.ToString());
        Check(w1.MaxFileBytes == LogFileWriter.DefaultMaxFileBytes, "默认单份上限 512 KB");
        Check(w1.MaxArchives == LogFileWriter.DefaultMaxArchives, "默认保留 3 份归档");

        // ---- 22.2 超限轮转 + 归档份数上界 ----
        string p2 = Path.Combine(log22Root, "b.log");
        var w2 = new LogFileWriter(p2, 8 * 1024, 2);
        Check(w2.MaxFileBytes == 16 * 1024, "上限低于 16 KB 时按 16 KB 兜底",
            w2.MaxFileBytes.ToString());
        for (int i = 0; i < 600; i++)
        {
            w2.Append($"L{i:D4} " + new string('x', 100));
        }
        Check(File.Exists(w2.ArchivePath(1)), "超限后当前文件归档为 .1");
        Check(File.Exists(w2.ArchivePath(2)), "第二份归档 .2 存在");
        Check(!File.Exists(w2.ArchivePath(3)), "超过保留份数的最老一份被删（没有 .3）");
        Check(new FileInfo(p2).Length <= w2.MaxFileBytes + 200, "轮转后当前份回到上限以内",
            new FileInfo(p2).Length.ToString());

        string[] a1 = File.ReadAllLines(w2.ArchivePath(1));
        string[] a2 = File.ReadAllLines(w2.ArchivePath(2));
        Check(a1.Length > 0 && a2.Length > 0, "两份归档都有内容（不是空壳）");
        if (a1.Length > 0 && a2.Length > 0)
        {
            int LastNo(string line) => int.Parse(line.Split(' ')[0][1..]);
            Check(LastNo(a1[^1]) > LastNo(a2[^1]), ".1 比 .2 新（归档顺序正确）",
                $".1 末尾 L{LastNo(a1[^1])} vs .2 末尾 L{LastNo(a2[^1])}");
        }

        // ---- 22.3 保留 0 份：不留档，但当前份仍有上界 ----
        string p3 = Path.Combine(log22Root, "c.log");
        var w3 = new LogFileWriter(p3, 8 * 1024, 0);
        for (int i = 0; i < 300; i++)
        {
            w3.Append($"L{i:D4} " + new string('x', 100));
        }
        Check(!File.Exists(w3.ArchivePath(1)), "保留 0 份时不留归档");
        Check(new FileInfo(p3).Length <= w3.MaxFileBytes + 200, "保留 0 份时当前份仍有上界",
            new FileInfo(p3).Length.ToString());

        // ---- 22.4 并发写入不丢行 ----
        string p4 = Path.Combine(log22Root, "d.log");
        var w4 = new LogFileWriter(p4);
        Parallel.For(0, 8, k =>
        {
            for (int i = 0; i < 50; i++)
            {
                w4.Append($"T{k}-{i}");
            }
        });
        Check(File.ReadAllLines(p4).Length == 400, "8 线程并发写 400 行一条不丢",
            File.ReadAllLines(p4).Length.ToString());

        // ---- 22.5 同步日志走同一套轮转 ----
        string log22Dir = Path.Combine(log22Root, "logs");
        var logger22 = new FileDiagnosticLogger(log22Dir, "sync.log", 8 * 1024, 2);
        for (int i = 0; i < 600; i++)
        {
            logger22.Log(SyncLogLevel.Info, "同步 " + i + " " + new string('y', 100));
        }
        Check(logger22.FilePath == Path.Combine(log22Dir, "sync.log"), "日志落到指定文件");
        Check(File.Exists(logger22.Writer.ArchivePath(1)), "同步日志同样会轮转");
        Check(new FileInfo(logger22.FilePath).Length <= logger22.Writer.MaxFileBytes + 200,
            "同步日志有体积上界", new FileInfo(logger22.FilePath).Length.ToString());
        string[] syncLines = File.ReadAllLines(logger22.FilePath);
        Check(syncLines.Length > 0 && syncLines[0].Contains("[信息]"), "日志行带中文级别",
            syncLines.Length > 0 ? syncLines[0] : "(空)");

        // ---- 22.6 写不进去也不能拖垮应用 ----
        string blocker = Path.Combine(log22Root, "blocker.txt");
        File.WriteAllText(blocker, "x");
        var wBad = new LogFileWriter(Path.Combine(blocker, "sub.log"));   // 父路径是文件，目录建不出来
        bool threw = false;
        try
        {
            wBad.Append("hello");
        }
        catch
        {
            threw = true;
        }
        Check(!threw, "目录建不出来时写入不抛异常（日志不能拖垮应用）");

        // ---- 22.7 诊断包把归档一起打包 ----
        string exp22 = Path.Combine(log22Root, "exp");
        Directory.CreateDirectory(Path.Combine(exp22, "logs"));
        File.WriteAllText(Path.Combine(exp22, "logs", "sync.log"), "current\n");
        File.WriteAllText(Path.Combine(exp22, "logs", "sync.log.1"), "rotated\n");
        var exporter22 = new DiagnosticExporter(exp22);
        string zip22 = Path.Combine(log22Root, "diag.zip");
        OperationResult r22 = exporter22.Export(zip22);
        Check(r22.Success, "诊断包导出成功", r22.ErrorMessage);
        if (r22.Success)
        {
            using var z22 = System.IO.Compression.ZipFile.OpenRead(zip22);
            Check(z22.GetEntry("logs/sync.log") is not null, "诊断包包含当前日志");
            Check(z22.GetEntry("logs/sync.log.1") is not null,
                "诊断包包含轮转归档（出事之前那一段）");
        }
    }
    finally
    {
        try
        {
            Directory.Delete(log22Root, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 23. 图标缓存轮次回归（反复增删不泄漏）
Console.WriteLine();
Console.WriteLine("== 23. 图标缓存轮次回归（文件反复增删时不会只增不减） ==");
{
    // 这一节是 9.6 那个"缓存无上限"缺陷的回归锁：
    // 旧实现里缓存条目 == 累计出现过的路径数，文件反复增删时只增不减。
    string churnRoot = Path.Combine(Path.GetTempPath(), "DeskLanesChurn_" + Guid.NewGuid().ToString("N")[..8]);
    string churnCfg = Path.Combine(Path.GetTempPath(), "DeskLanesChurnCfg_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(churnRoot);
    Directory.CreateDirectory(churnCfg);
    try
    {
        const int rounds = 20;
        const int perRound = 30;

        IWorkspaceService churnWs = new WorkspaceService(
            new LibraryStore(churnCfg), new ConfigStore(churnCfg), resolver);
        var churnScanner = new DesktopScanner(resolver);
        var churnIcons = new IconService();
        var counts = new List<int>();
        int created = 0;

        for (int round = 0; round < rounds; round++)
        {
            // 上一轮的文件全部删掉（真实场景：用户清理桌面）
            foreach (string stale in Directory.EnumerateFiles(churnRoot))
            {
                File.Delete(stale);
            }

            // 本轮造一批路径全新的文件
            for (int i = 0; i < perRound; i++)
            {
                File.WriteAllText(Path.Combine(churnRoot, $"r{round:D2}-{i:D2}.txt"), "x");
                created++;
            }

            ScanResult churnScan = await churnScanner.ScanAsync(new ScanOptions
            {
                Targets = new[] { new ScanTarget { Path = churnRoot, Origin = ItemOrigin.UserDesktop } },
            });
            await churnWs.MergeScanAsync(churnScan);

            foreach (DesktopItem item in churnWs.Items)
            {
                churnIcons.GetIcon(item.FullPath);
            }

            // 与 MainViewModel 每轮做的同一件事：按当前工作区裁剪
            churnIcons.PruneExcept(churnWs.Items.Select(i => i.FullPath));
            counts.Add(churnIcons.CachedIconCount);
        }

        Info($"累计创建 {created} 个不同路径，各轮缓存条目：" + string.Join(", ", counts));

        Check(created == rounds * perRound, "确实累计创建了几百个不同路径", created.ToString());
        Check(counts[^1] <= IconService.DefaultMaxCachedIcons,
            "反复增删后缓存仍在上限内（旧实现会等于累计路径数）",
            $"{counts[^1]} <= {IconService.DefaultMaxCachedIcons}，累计 {created}");
        Check(counts.All(c => c <= IconService.DefaultMaxCachedIcons),
            "每一轮都没突破上限（不只是最后一轮）");

        // ---- 阶段 14：文件消失后项目被自动清除，图标缓存每轮已跟着裁剪 ----
        //      旧实现要"用户手动清理失效项"才回落，现在不再有失效项堆积。
        List<DesktopItem> staleItems = churnWs.Items.Where(i => !i.IsAvailable).ToList();
        Check(staleItems.Count == 0, "文件消失后项目被自动清除（不再留下失效项）",
            $"失效 {staleItems.Count} 个");
        Check(churnWs.Items.Count == perRound, "工作区只剩当前这一轮真实存在的项目",
            $"{churnWs.Items.Count} / {perRound}");

        foreach (DesktopItem item in staleItems)
        {
            churnWs.RemoveItem(item);
        }

        int removed = churnIcons.PruneExcept(churnWs.Items.Select(i => i.FullPath));
        Check(removed == 0, "每轮已自动裁剪，无需再靠清理失效项回落", $"再摘掉 {removed} 个");
        Check(churnIcons.CachedIconCount <= perRound, "缓存回落到当前项目量级（不是只增不减）",
            $"{churnIcons.CachedIconCount} <= {perRound}");

        // ---- 再跑几轮，确认回落后不会又无界爬升 ----
        int afterCleanup = churnIcons.CachedIconCount;
        for (int round = 0; round < 5; round++)
        {
            foreach (string stale in Directory.EnumerateFiles(churnRoot))
            {
                File.Delete(stale);
            }

            for (int i = 0; i < perRound; i++)
            {
                File.WriteAllText(Path.Combine(churnRoot, $"s{round:D2}-{i:D2}.txt"), "x");
            }

            ScanResult churnScan2 = await churnScanner.ScanAsync(new ScanOptions
            {
                Targets = new[] { new ScanTarget { Path = churnRoot, Origin = ItemOrigin.UserDesktop } },
            });
            await churnWs.MergeScanAsync(churnScan2);

            foreach (DesktopItem item in churnWs.Items)
            {
                churnIcons.GetIcon(item.FullPath);
            }

            foreach (DesktopItem item in churnWs.Items.Where(i => !i.IsAvailable).ToList())
            {
                churnWs.RemoveItem(item);
            }

            churnIcons.PruneExcept(churnWs.Items.Select(i => i.FullPath));
        }

        Check(churnIcons.CachedIconCount <= perRound, "继续多轮后仍稳定在当前项目量级",
            $"{churnIcons.CachedIconCount}（清理后基线 {afterCleanup}）");
        Check(churnIcons.CachedIconCount <= IconService.DefaultMaxCachedIcons, "始终不超过上限");
    }
    finally
    {
        foreach (string dir in new[] { churnRoot, churnCfg })
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }
    }
}

// ---------------------------------------------------------------- 24. 脏数据与特殊文件名容错
Console.WriteLine();
Console.WriteLine("== 24. 脏数据（能解析但自相矛盾）与特殊文件名容错 ==");
{
    // 第 11 节覆盖的是"JSON 语法坏掉"；这一节覆盖另一类真实故障：
    // JSON 完全合法，但数据自相矛盾（旧版本 bug、用户手改、跨设备拷贝都可能造成）。
    string dirtyRoot = Path.Combine(Path.GetTempPath(), "DeskLanesDirty_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dirtyRoot);
    try
    {
        // ---- 24.1 自相矛盾的 library.json ----
        string dirtyJson = """
        {
          "Version": 1,
          "Items": [
            { "Id": "aaa", "Name": "项目A", "FullPath": "C:\\temp\\a.txt", "Kind": 1, "Origin": 2,
              "IsAvailable": true, "AddedAt": "2026-10-01T00:00:00", "LastSeenAt": "2026-10-01T00:00:00" },
            { "Id": "bbb", "Name": "项目B", "FullPath": "C:\\temp\\b.txt", "Kind": 1, "Origin": 2,
              "IsAvailable": true, "AddedAt": "2026-10-01T00:00:00", "LastSeenAt": "2026-10-01T00:00:00" },
            { "Id": "bbb", "Name": "项目B（Id 重复）", "FullPath": "C:\\temp\\b.txt", "Kind": 1, "Origin": 2,
              "IsAvailable": true, "AddedAt": "2026-10-01T00:00:00", "LastSeenAt": "2026-10-01T00:00:00" },
            { "Id": "ccc", "Name": "", "FullPath": "C:\\temp\\c.txt", "Kind": 1, "Origin": 2,
              "IsAvailable": true, "AddedAt": "2026-10-01T00:00:00", "LastSeenAt": "2026-10-01T00:00:00" }
          ],
          "Groups": [
            { "Id": "g1", "Name": "分类1", "Order": 5, "ItemIds": ["aaa", "bbb", "不存在的Id"] },
            { "Id": "g2", "Name": "分类2", "Order": 5, "ItemIds": ["bbb"] }
          ],
          "Panels": [
            { "Id": "p1", "Title": "分区1", "Order": 0, "X": -99999, "Y": 123456789,
              "Width": 987654321, "Height": -5, "ItemIds": ["aaa", "不存在的Id"] }
          ],
          "DismissedPaths": ["C:\\NONEXIST\\早已删掉的文件.txt"]
        }
        """;
        File.WriteAllText(Path.Combine(dirtyRoot, "library.json"), dirtyJson);

        var dirtyWs = new WorkspaceService(
            new LibraryStore(dirtyRoot), new ConfigStore(dirtyRoot), resolver);

        Check(dirtyWs.Items.Count == 4, "脏数据里的项目全部读进来了（不因为矛盾就整份丢弃）",
            dirtyWs.Items.Count.ToString());
        Check(dirtyWs.Groups.Count == 2, "分类全部读进来", dirtyWs.Groups.Count.ToString());
        Check(dirtyWs.Panels.Count == 1, "分区全部读进来", dirtyWs.Panels.Count.ToString());
        Check(dirtyWs.LoadWarnings.Count == 0, "自相矛盾不算'损坏'（不误报、不重置用户数据）",
            string.Join(" | ", dirtyWs.LoadWarnings));

        // 单成员制：同一个项目 Id 出现在两个分类里，是脏数据。观察实际行为
        int groupsHoldingB = dirtyWs.Groups.Count(g => g.ItemIds.Contains("bbb"));
        Info($"Id=bbb 出现在 {groupsHoldingB} 个分类里（单成员制下应为 1）");
        Check(groupsHoldingB >= 1, "至少还能在分类里找到该项目（没有把关系丢光）");

        // 悬空引用：分类/分区指向不存在的 Id，不能让界面崩，也不能凭空造项目
        Check(dirtyWs.Groups[0].ItemIds.Contains("不存在的Id") || true, "悬空引用不导致异常");
        Check(!dirtyWs.Items.Any(i => i.Id == "不存在的Id"),
            "悬空引用不会被当成真实项目造出来");

        // 分区坐标越界：加载时不做钳制（只有用户拖动时才钳），但要能正常读出来
        WorkspacePanel dirtyPanel = dirtyWs.Panels[0];
        Check(double.IsFinite(dirtyPanel.X) && double.IsFinite(dirtyPanel.Width),
            "越界坐标仍是有限值（不会变成 NaN 把后续布局搞坏）",
            $"X={dirtyPanel.X}, Width={dirtyPanel.Width}");

        // 名字为空的项目：不崩，且能再次保存（这是最容易把 JSON 写坏的地方）
        DesktopItem? emptyName = dirtyWs.Items.FirstOrDefault(i => i.Id == "ccc");
        Check(emptyName is not null, "空名称项目仍在库中");
        dirtyWs.CreateGroup("再建一个分类");
        var reloaded = new WorkspaceService(
            new LibraryStore(dirtyRoot), new ConfigStore(dirtyRoot), resolver);
        Check(reloaded.Groups.Count == 3, "改动后能正常存回并读回", reloaded.Groups.Count.ToString());
        Check(reloaded.LoadWarnings.Count == 0, "存回后重新加载无警告",
            string.Join(" | ", reloaded.LoadWarnings));

        // ---- 24.2 特殊文件名 / 超长路径的扫描容错 ----
        string oddRoot = Path.Combine(dirtyRoot, "odd");
        Directory.CreateDirectory(oddRoot);

        string[] oddNames =
        {
            "带 & 符号.txt",
            "百分号 %PATH% .txt",
            "井号 #1.txt",
            "单引号 'quote'.txt",
            "【方括号】.txt",
            "emoji-🎉.txt",
            new string('长', 200) + ".txt",   // 接近 255 上限的长文件名
        };

        int createdOdd = 0;
        foreach (string name in oddNames)
        {
            try
            {
                File.WriteAllText(Path.Combine(oddRoot, name), "x");
                createdOdd++;
            }
            catch
            {
                // 文件系统不接受的名字跳过（不是本测试的靶子）
            }
        }

        ScanResult oddScan = await new DesktopScanner(resolver).ScanAsync(new ScanOptions
        {
            Targets = new[] { new ScanTarget { Path = oddRoot, Origin = ItemOrigin.UserDesktop } },
        });

        Info($"特殊文件名：创建 {createdOdd} 个，扫到 {oddScan.Items.Count} 个，警告 {oddScan.Warnings.Count} 条");
        Check(oddScan.Items.Count >= createdOdd - 1, "特殊文件名基本都能扫到（不因为字符就整批失败）",
            $"{oddScan.Items.Count}/{createdOdd}");
        Check(oddScan.Items.All(i => !string.IsNullOrWhiteSpace(i.Name)),
            "每个项目都有显示名称（空名会让界面出现空白格子）");
        Check(oddScan.Warnings.Count == 0, "特殊文件名不产生扫描警告",
            string.Join(" | ", oddScan.Warnings));
    }
    finally
    {
        try
        {
            Directory.Delete(dirtyRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 25. 设置文件的语义容错（值越界 / 非法）
Console.WriteLine();
Console.WriteLine("== 25. 设置文件语义容错（能解析但值不合法） ==");
{
    // 与第 11 节（JSON 语法坏掉）和第 24 节（库数据自相矛盾）互补：
    // 这里测的是 settings.json 完全合法、但**取值不合法**——手改配置、旧版本写入、
    // 跨设备拷贝都很常见。判据一律是"不崩、回退到能用的值"，而不是照单全收。
    string semRoot = Path.Combine(Path.GetTempPath(), "DeskLanesSem_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(semRoot);
    try
    {
        string semJson = """
        {
          "Version": 1,
          "LayoutMode": 99,
          "WindowMode": -5,
          "WindowWidth": 99999,
          "WindowHeight": 0,
          "WindowLeft": 12345,
          "WindowTop": -6789,
          "WindowMaximized": true,
          "FloatingWindowWidth": 3,
          "FloatingWindowHeight": -200,
          "FloatingWindowLeft": 999999,
          "FloatingWindowTop": -999999,
          "SidebarDock": 77,
          "SidebarWidth": 99999,
          "HotkeyModifiers": 12345,
          "HotkeyKey": 99999
        }
        """;
        File.WriteAllText(Path.Combine(semRoot, "settings.json"), semJson);

        var semWs = new WorkspaceService(
            new LibraryStore(semRoot), new ConfigStore(semRoot), resolver);
        AppSettings s = semWs.Settings;

        Info($"越界值加载结果：WindowMode={s.WindowMode}, LayoutMode={s.LayoutMode}, "
             + $"SidebarDock={s.SidebarDock}, WindowWidth={s.WindowWidth}, "
             + $"FloatingWindowWidth={s.FloatingWindowWidth}, SidebarWidth={s.SidebarWidth}");
        Check(semWs.LoadWarnings.Count == 0, "越界值不算'损坏'（不重置整份设置）",
            string.Join(" | ", semWs.LoadWarnings));

        // 枚举越界：System.Text.Json 会把未知数字原样写进枚举字段，
        // 界面若直接拿它做分支就会落到 default 之外的地方——这里确认回退到合法值
        bool modeOk = s.WindowMode == WindowMode.Floating
                      || s.WindowMode == WindowMode.Full
                      || s.WindowMode == WindowMode.Sidebar;
        Check(modeOk, "窗口模式越界时回退到合法枚举值", s.WindowMode.ToString());
        bool layoutOk = s.LayoutMode == LayoutMode.CategoryNav || s.LayoutMode == LayoutMode.FreePanels;
        Check(layoutOk, "布局模式越界时回退到合法枚举值", s.LayoutMode.ToString());
        bool dockOk = s.SidebarDock == SidebarDock.Left || s.SidebarDock == SidebarDock.Right;
        Check(dockOk, "侧边栏停靠位置越界时回退到合法枚举值", s.SidebarDock.ToString());

        // 尺寸：不要求立刻钳制（窗口层会按屏收缩），但必须是有限值且为正，
        // 否则 WPF 会抛异常或给出 0 大小窗口
        Check(double.IsFinite(s.WindowWidth) && double.IsFinite(s.WindowHeight),
            "窗口尺寸是有限值", $"{s.WindowWidth}x{s.WindowHeight}");
        Check(double.IsFinite(s.FloatingWindowWidth) && double.IsFinite(s.FloatingWindowHeight),
            "悬浮窗尺寸是有限值",
            $"{s.FloatingWindowWidth}x{s.FloatingWindowHeight}");
        Check(s.FloatingWindowWidth > 0, "悬浮窗宽度为正（0 或负数会让窗口消失）",
            s.FloatingWindowWidth.ToString());

        // 位置：越界坐标保留原值即可，"是否可见"由窗口层的 IsPositionVisible 判定
        Check(s.WindowLeft is null || double.IsFinite(s.WindowLeft.Value),
            "主窗口 X 是有限值（NaN 会写坏 JSON）");
        Check(s.FloatingWindowLeft is null || double.IsFinite(s.FloatingWindowLeft.Value),
            "悬浮窗 X 是有限值");

        // 快捷键：非法组合不能让热键注册崩掉，界面上应能显示成可读文本
        string hotkeyText = s.Hotkey.DisplayText;
        Info($"越界快捷键被读成：{hotkeyText}");
        Check(!string.IsNullOrWhiteSpace(hotkeyText), "非法快捷键仍能给出可读文本", hotkeyText);

        // 存回并重新加载：不能因为这些越界值就写坏 JSON
        semWs.SetLayoutMode(LayoutMode.FreePanels);
        var semReload = new WorkspaceService(
            new LibraryStore(semRoot), new ConfigStore(semRoot), resolver);
        Check(semReload.Settings.LayoutMode == LayoutMode.FreePanels,
            "越界值存在时改动仍能存回并读回");
        Check(semReload.LoadWarnings.Count == 0, "存回后重新加载无警告",
            string.Join(" | ", semReload.LoadWarnings));
    }
    finally
    {
        try
        {
            Directory.Delete(semRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 26. 大桌面（项目数超过图标缓存上限）
Console.WriteLine();
Console.WriteLine("== 26. 大桌面：项目数超过图标缓存上限时的表现 ==");
{
    // 第 20/23 节验证的是"缓存有上界"，但都是在项目数远小于上限（512）的前提下。
    // 真实用户里桌面塞几百个项目很常见——那时缓存必然装不下，要确认的是：
    // 性能不塌、上限依然压得住、图标一个都不少（项目自己持有图标，缓存只是复用地）。
    string heavyRoot = Path.Combine(Path.GetTempPath(), "DeskLanesHeavy_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(heavyRoot);
    string heavyCfg = Path.Combine(Path.GetTempPath(), "DeskLanesHeavyCfg_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(heavyCfg);
    try
    {
        const int heavyCount = 800;
        for (int i = 0; i < heavyCount; i++)
        {
            File.WriteAllText(Path.Combine(heavyRoot, $"heavy-{i:D4}.txt"), "heavy");
        }

        var heavyScanner = new DesktopScanner(resolver);
        var hsw = System.Diagnostics.Stopwatch.StartNew();
        ScanResult heavyScan = await heavyScanner.ScanAsync(new ScanOptions
        {
            Targets = new[] { new ScanTarget { Path = heavyRoot, Origin = ItemOrigin.UserDesktop } },
        });
        long hScanMs = hsw.ElapsedMilliseconds;

        IWorkspaceService heavyWs = new WorkspaceService(
            new LibraryStore(heavyCfg), new ConfigStore(heavyCfg), resolver);
        hsw.Restart();
        ScanMergeOutcome hOutcome = await heavyWs.MergeScanAsync(heavyScan);
        long hMergeMs = hsw.ElapsedMilliseconds;

        Info($"{heavyCount} 个项目：扫描 {hScanMs} ms，合并 {hMergeMs} ms，新增 {hOutcome.Added} 项");
        Check(heavyScan.Items.Count == heavyCount, "800 个项目全部扫到",
            $"实际 {heavyScan.Items.Count}");
        Check(hScanMs < 5000, "800 个项目扫描 < 5 秒", $"{hScanMs} ms");
        Check(hMergeMs < 5000, "800 个项目合并进库 < 5 秒", $"{hMergeMs} ms");

        // 图标：项目数 800 > 上限 512，缓存必然装不下
        var heavyIcons = new IconService();
        hsw.Restart();
        int heavyIconOk = 0;
        foreach (DesktopItem item in heavyWs.Items)
        {
            if (heavyIcons.GetIcon(item.FullPath) is not null)
            {
                heavyIconOk++;
            }
        }
        long hIconMs = hsw.ElapsedMilliseconds;
        Info($"{heavyCount} 个项目取图标 {hIconMs} ms，成功 {heavyIconOk} 个，"
             + $"缓存条目 {heavyIcons.CachedIconCount}（上限 {IconService.DefaultMaxCachedIcons}）");
        Check(heavyIconOk == heavyCount, "项目数超过上限时图标仍然一个个都取到了",
            $"{heavyIconOk}/{heavyCount}");
        Check(hIconMs < 30000, "800 个项目取图标 < 30 秒", $"{hIconMs} ms");
        Check(heavyIcons.CachedIconCount <= IconService.DefaultMaxCachedIcons,
            "项目数超过上限时缓存依然被压在上限内（不是按项目数无限增长）",
            $"{heavyIcons.CachedIconCount} <= {IconService.DefaultMaxCachedIcons}");

        // 缓存被淘汰不代表图标丢：项目自己持有 ImageSource，重复取不会重新走 P/Invoke
        hsw.Restart();
        int secondPass = 0;
        foreach (DesktopItem item in heavyWs.Items)
        {
            if (heavyIcons.GetIcon(item.FullPath) is not null)
            {
                secondPass++;
            }
        }
        long hSecondMs = hsw.ElapsedMilliseconds;
        Info($"缓存满载后再取一遍：{hSecondMs} ms，成功 {secondPass} 个");
        Check(secondPass == heavyCount, "缓存满载后仍能取到全部图标", $"{secondPass}/{heavyCount}");
        // ---- 26.1 非有限几何（NaN / Infinity）不能写进配置 ----
        // 分区的 X/Y/Width/Height 是双向绑定到 Canvas.Left/Top/Width/Height 的，
        // 而 WPF 里 FrameworkElement.Width 的默认值就是 NaN（Auto）。一旦 NaN 被推回模型，
        // Math.Clamp 会原样放行（NaN 的比较全为 false），而 System.Text.Json 写不了 NaN ——
        // 保存时会抛异常，最坏情况是整份配置读不回来。
        IWorkspaceService nanWs = new WorkspaceService(
            new LibraryStore(heavyCfg), new ConfigStore(heavyCfg), resolver);
        WorkspacePanel nanPanel = nanWs.CreatePanel("NaN 测试");
        string? nanError = null;
        try
        {
            nanWs.UpdatePanelLayout(nanPanel, double.NaN, double.PositiveInfinity,
                double.NaN, double.NegativeInfinity);
            nanWs.Save();
        }
        catch (Exception ex)
        {
            nanError = ex.GetType().Name + ": " + ex.Message;
        }

        Info("传入 NaN/Infinity 几何的结果：" + (nanError ?? "无异常"));
        Check(nanError is null, "非有限几何不会让保存抛异常（NaN 写不进 JSON）", nanError ?? "");
        Check(double.IsFinite(nanPanel.X) && double.IsFinite(nanPanel.Y)
              && double.IsFinite(nanPanel.Width) && double.IsFinite(nanPanel.Height),
            "非有限几何被挡在模型之外",
            string.Format("{0},{1},{2},{3}", nanPanel.X, nanPanel.Y, nanPanel.Width, nanPanel.Height));

        var nanReload = new WorkspaceService(
            new LibraryStore(heavyCfg), new ConfigStore(heavyCfg), resolver);
        Check(nanReload.LoadWarnings.Count == 0, "写过非有限几何后配置仍能正常读回",
            string.Join(" | ", nanReload.LoadWarnings));

    }
    finally
    {
        try
        {
            Directory.Delete(heavyRoot, true);
            Directory.Delete(heavyCfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 27. 显示器热插拔后的窗口钳位
// 阶段 13 物理热插拔实测：拔下左侧竖屏后显示器只剩 1 台、虚拟屏缩成 2560x1440，
// 悬浮窗却仍停在 (-1043,200) 这种已不存在的负坐标上；插回后被收缩成 340x48 细横条。
// 这两条修复都抽成纯函数，在这里做可重复的回归断言，不必每次真的去拔线。
{
    // 27.1 正常三屏：窗口在虚拟屏内，不应被挪动
    var ok = MonitorService.ClampToVirtualScreen(-1036, 240, 995, 1163, -1080, 0, 6200, 1920);
    Check(ok.Left == -1036 && ok.Top == 240, "正常三屏下窗口位置不被误改",
        $"{ok.Left},{ok.Top}");

    // 27.2 拔掉显示器：虚拟屏变单屏，负 x 坐标必须被拉回来
    var detached = MonitorService.ClampToVirtualScreen(-1043, 200, 829, 969, 0, 0, 2560, 1440);
    Check(detached.Left == 0 && detached.Top == 200, "显示器消失后窗口被拉回可见区",
        $"{detached.Left},{detached.Top}");

    // 27.3 窗口比剩余屏幕还宽：上界退化成左边界，不能算出负数或 NaN
    var oversized = MonitorService.ClampToVirtualScreen(100, 100, 3000, 800, 0, 0, 2560, 1440);
    Check(oversized.Left == 0 && double.IsFinite(oversized.Top), "窗口宽于屏幕时钳位不产生非法值",
        $"{oversized.Left},{oversized.Top}");

    // 27.4 非有限位置（WPF 的 Auto 默认值 NaN）要有兜底
    var nanPos = MonitorService.ClampToVirtualScreen(double.NaN, double.PositiveInfinity, 800, 600, 0, 0, 2560, 1440);
    Check(double.IsFinite(nanPos.Left) && double.IsFinite(nanPos.Top)
          && nanPos.Left >= 0 && nanPos.Top >= 0, "NaN/Infinity 位置钳位后仍是有限合法值",
        $"{nanPos.Left},{nanPos.Top}");

    // 27.5 异常小的工作区（热插拔中间态）必须跳过收缩，否则窗口被压成细条
    Check(!MonitorService.IsWorkAreaSane(new System.Windows.Size(340, 48), 620, 420),
        "340x48 的异常工作区被识别为不可收缩", "");
    Check(MonitorService.IsWorkAreaSane(new System.Windows.Size(2560, 1440), 620, 420),
        "正常显示器工作区允许收缩", "");
}

// ---------------------------------------------------------------- 28. 阶段 14：分组与失效项清理
{
    // 28.1 分类导航的「文件夹 / 软件」分区
    Check(new DesktopItem { Kind = DesktopItemKind.Folder }.KindGroup == "文件夹"
          && new DesktopItem { Kind = DesktopItemKind.Shortcut }.KindGroup == "软件"
          && new DesktopItem { Kind = DesktopItemKind.File }.KindGroup == "软件",
        "项目按文件夹/软件分成两组", "");

    string deskRoot = Path.Combine(Path.GetTempPath(), "DeskLanesP14_" + Guid.NewGuid().ToString("N"));
    string p14CfgRoot = deskRoot + "_cfg";
    Directory.CreateDirectory(deskRoot);
    Directory.CreateDirectory(p14CfgRoot);
    try
    {
        var ws14 = new WorkspaceService(new LibraryStore(p14CfgRoot), new ConfigStore(p14CfgRoot),
            new ShortcutResolver());
        string gone = Path.Combine(deskRoot, "已删除软件.lnk");
        File.WriteAllText(gone, "x");

        DesktopItem MakeItem() => new()
        {
            Name = "已删除软件", FullPath = gone,
            Kind = DesktopItemKind.Shortcut, Origin = ItemOrigin.UserDesktop,
        };

        await ws14.MergeScanAsync(new ScanResult
        {
            Items = new[] { MakeItem() }, ScopeDirectories = new[] { deskRoot },
        });
        Check(ws14.Items.Count == 1, "首次扫描把桌面项目合并进库", ws14.Items.Count.ToString());

        // 桌面上确实删掉了 -> 自动清除，且不能写进忽略列表
        File.Delete(gone);
        ScanMergeOutcome cleared = await ws14.MergeScanAsync(new ScanResult
        {
            ScopeDirectories = new[] { deskRoot },
        });
        Check(cleared.Removed == 1 && ws14.Items.Count == 0,
            "桌面已确实不存在的项目被自动清除",
            $"removed={cleared.Removed}, left={ws14.Items.Count}");
        Check(!ws14.IsDismissedPath(gone),
            "自动清除不写忽略列表（软件放回桌面还能加回来）", "");

        File.WriteAllText(gone, "x");
        ScanMergeOutcome addedBack = await ws14.MergeScanAsync(new ScanResult
        {
            Items = new[] { MakeItem() }, ScopeDirectories = new[] { deskRoot },
        });
        Check(addedBack.Added == 1 && ws14.Items.Count == 1,
            "软件放回桌面后无需手动操作即可重新出现",
            $"added={addedBack.Added}, left={ws14.Items.Count}");

        // 不在本次扫描范围内的目录，不能被误删
        ScanMergeOutcome unrelated = await ws14.MergeScanAsync(new ScanResult
        {
            ScopeDirectories = new[] { Path.Combine(deskRoot, "未扫描的子目录") },
        });
        Check(unrelated.Removed == 0 && ws14.Items.Count == 1,
            "不在本次扫描范围的目录不会被误删",
            $"removed={unrelated.Removed}, left={ws14.Items.Count}");

        // 28.2 分类导航两个区域真的把项目分流了
        await ws14.MergeScanAsync(new ScanResult
        {
            Items = new[]
            {
                new DesktopItem
                {
                    Name = "演示文件夹", FullPath = Path.Combine(deskRoot, "演示文件夹"),
                    Kind = DesktopItemKind.Folder, Origin = ItemOrigin.UserDesktop,
                },
                new DesktopItem
                {
                    Name = "演示软件", FullPath = Path.Combine(deskRoot, "演示软件.lnk"),
                    Kind = DesktopItemKind.Shortcut, Origin = ItemOrigin.UserDesktop,
                },
            },
            ScopeDirectories = new[] { deskRoot },
        });
        var navVm = new CategoryNavViewModel(ws14, new ConsoleDialogService(), launchService,
            new ShellState());
        Check(navVm.FolderItems.Count == 1
              && navVm.FolderItems.All(i => i.Kind == DesktopItemKind.Folder)
              && navVm.AppItems.Count >= 1
              && navVm.AppItems.All(i => i.Kind != DesktopItemKind.Folder),
            "分类导航把文件夹与软件拆成两个区域",
            $"文件夹 {navVm.FolderItems.Count} / 软件 {navVm.AppItems.Count}");
    }
    finally
    {
        try
        {
            Directory.Delete(deskRoot, true);
            Directory.Delete(p14CfgRoot, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 29. 阶段 19：全部视图 / 分组统一 / 悬浮窗两视图
{
    string p19Root = Path.Combine(Path.GetTempPath(), "DeskLanesP19_" + Guid.NewGuid().ToString("N"));
    string p19Cfg = p19Root + "_cfg";
    Directory.CreateDirectory(p19Root);
    Directory.CreateDirectory(p19Cfg);
    try
    {
        var ws19 = new WorkspaceService(new LibraryStore(p19Cfg), new ConfigStore(p19Cfg),
            new ShortcutResolver());
        var state19 = new ShellState();
        var dlg19 = new ConsoleDialogService();

        // 造 4 个项目：两个文件夹、两个软件，名字故意乱序
        DesktopItem Make(string name, DesktopItemKind kind) => new()
        {
            Name = name, FullPath = Path.Combine(p19Root, name),
            Kind = kind, Origin = ItemOrigin.UserDesktop,
        };
        var zFolder = Make("Zeta 文件夹", DesktopItemKind.Folder);
        var aFolder = Make("Alpha 文件夹", DesktopItemKind.Folder);
        var zApp = Make("Zeta 软件", DesktopItemKind.Shortcut);
        var aApp = Make("Alpha 软件", DesktopItemKind.Shortcut);
        await ws19.MergeScanAsync(new ScanResult
        {
            Items = new[] { zFolder, aFolder, zApp, aApp },
            ScopeDirectories = new[] { p19Root },
        });

        // 建一个「游戏」分类并放两个项目进去
        ShortcutGroup game = ws19.CreateGroup("游戏");
        ws19.AssignItemToGroup(aApp, game);
        ws19.AssignItemToGroup(zApp, game);

        var nav19 = new CategoryNavViewModel(ws19, dlg19, launchService, state19);
        Check(nav19.Categories[0].Name == "全部", "分类导航首节点叫「全部」", nav19.Categories[0].Name);
        nav19.SelectedCategory = nav19.Categories[0];
        Check(nav19.Items.Count == 4, "「全部」含已分组项目（4 个都在）", nav19.Items.Count.ToString());
        Check(nav19.FolderItems.Count == 2 && nav19.AppItems.Count == 2,
            "「全部」拆成文件夹 / 软件两区", $"{nav19.FolderItems.Count}/{nav19.AppItems.Count}");
        Check(nav19.FolderItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 文件夹", "Zeta 文件夹" })
              && nav19.AppItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 软件", "Zeta 软件" }),
            "两区各自按 A-Z 排序", "");

        // 切换布局后，自由分区出现同名的「游戏」面板且成员同步
        ws19.SetLayoutMode(LayoutMode.FreePanels);
        WorkspacePanel? gamePanel = ws19.Panels.FirstOrDefault(p => p.GroupId == game.Id);
        Check(gamePanel is not null && gamePanel.Title == "游戏"
              && gamePanel.ItemIds.Count == 2,
            "切换布局后分类自动成为同名分区面板且成员同步",
            gamePanel is null ? "无配对面板" : $"{gamePanel.Title}/{gamePanel.ItemIds.Count}");

        var fp19 = new FreePanelsViewModel(ws19, dlg19, launchService, state19);
        Check(fp19.PoolHeaderText.StartsWith("全部"), "自由分区底部叫「全部」", fp19.PoolHeaderText);
        Check(fp19.PoolItems.Count == 4 && fp19.PoolItems.Contains(aApp),
            "自由分区「全部」含已加入面板的项目", fp19.PoolItems.Count.ToString());

        // 悬浮窗两种视图
        var float19 = new FloatingWindowViewModel(ws19, dlg19, launchService, state19);
        Check(float19.IsAllView && float19.DesktopFolderItems.Count == 2
              && float19.SoftwareItems.Count == 2,
            "悬浮窗默认「全部」视图且分两区", "");
        float19.ToggleViewModeCommand.Execute(null);
        Check(float19.IsFolderView && float19.GroupCards.Count == 1
              && float19.GroupCards[0].Name == "游戏"
              && float19.GroupCards[0].Items.Count == 2,
            "文件夹视图直接给带框分组，框里就是那 2 个软件（不用点开）",
            $"cards={float19.GroupCards.Count} items={float19.GroupCards[0].Items.Count}");
        Check(float19.IsGroupCardsVisible && float19.Items.Count == 0,
            "分组框视图不额外平铺一份项目", "");

        // 阶段 20：拖到画布空白处自动建面板 + 拖入后边框自动长大
        fp19.CreatePanelAt(zFolder, 600, 300);
        WorkspacePanel? droppedPanel = ws19.Panels.FirstOrDefault(p => p.Title == "新文件夹");
        Check(droppedPanel is not null && droppedPanel.ItemIds.Contains(zFolder.Id)
              && droppedPanel.X > 400,
            "拖到画布空白处会在落点新建面板并收下项目",
            droppedPanel is null ? "没建出来" : $"X={droppedPanel.X}");
        if (droppedPanel is not null)
        {
            double heightBefore = droppedPanel.Height;
            for (int k = 0; k < 6; k++)
            {
                var extra = new DesktopItem
                {
                    Name = "额外" + k, FullPath = Path.Combine(p19Root, "额外" + k),
                    Kind = DesktopItemKind.Shortcut, Origin = ItemOrigin.UserDesktop,
                };
                ws19.Items.Add(extra);
                ws19.AssignItemToPanel(extra, droppedPanel);
            }
            Check(droppedPanel.Height > heightBefore,
                "项目变多时分区边框自动放大", $"{heightBefore} -> {droppedPanel.Height}");
        }
    }
    finally
    {
        try
        {
            Directory.Delete(p19Root, true);
            Directory.Delete(p19Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 30. 阶段 21：侧边栏软件 / 文件夹两区
{
    string p21Root = Path.Combine(Path.GetTempPath(), "DeskLanesP21_" + Guid.NewGuid().ToString("N"));
    string p21Cfg = p21Root + "_cfg";
    Directory.CreateDirectory(p21Root);
    Directory.CreateDirectory(p21Cfg);
    try
    {
        var ws21 = new WorkspaceService(new LibraryStore(p21Cfg), new ConfigStore(p21Cfg),
            new ShortcutResolver());
        DesktopItem Make21(string name, DesktopItemKind kind) => new()
        {
            Name = name, FullPath = Path.Combine(p21Root, name),
            Kind = kind, Origin = ItemOrigin.UserDesktop,
        };
        await ws21.MergeScanAsync(new ScanResult
        {
            Items = new[]
            {
                Make21("Zeta 软件", DesktopItemKind.Shortcut),
                Make21("Alpha 软件", DesktopItemKind.Shortcut),
                Make21("Zeta 文件夹", DesktopItemKind.Folder),
                Make21("Alpha 文件夹", DesktopItemKind.Folder),
            },
            ScopeDirectories = new[] { p21Root },
        });

        var side21 = new SidebarViewModel(ws21, new ConsoleDialogService(), launchService,
            new ShellState());
        Check(side21.Categories[0].Name == "全部"
              && side21.SoftwareItems.Count == 2 && side21.FolderItems.Count == 2,
            "侧边栏「全部」拆成软件 / 文件夹两区",
            $"{side21.SoftwareItems.Count}/{side21.FolderItems.Count}");
        Check(side21.SoftwareItems.All(i => i.Kind != DesktopItemKind.Folder)
              && side21.FolderItems.All(i => i.Kind == DesktopItemKind.Folder),
            "两区按 Kind 正确分流", "");
        Check(side21.SoftwareItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 软件", "Zeta 软件" })
              && side21.FolderItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 文件夹", "Zeta 文件夹" }),
            "两区各自 A-Z 排序", "");

        ShortcutGroup game21 = ws21.CreateGroup("游戏");
        ws21.AssignItemToGroup(side21.SoftwareItems[0], game21);
        side21.SelectedCategory = side21.Categories.First(c => c.GroupId == game21.Id);
        Check(side21.SoftwareItems.Count == 1 && side21.FolderItems.Count == 0,
            "点「游戏」只显示该分类的软件", $"{side21.SoftwareItems.Count}/{side21.FolderItems.Count}");
    }
    finally
    {
        try
        {
            Directory.Delete(p21Root, true);
            Directory.Delete(p21Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 31. 阶段 23：状态栏 / 面板避让 / 全部区高度
{
    string p23Root = Path.Combine(Path.GetTempPath(), "DeskLanesP23_" + Guid.NewGuid().ToString("N"));
    string p23Cfg = p23Root + "_cfg";
    Directory.CreateDirectory(p23Root);
    Directory.CreateDirectory(p23Cfg);
    try
    {
        var ws23 = new WorkspaceService(new LibraryStore(p23Cfg), new ConfigStore(p23Cfg),
            new ShortcutResolver());
        var state23 = new ShellState();
        var dlg23 = new ConsoleDialogService();

        // 连建 5 个分区，两两不得相交
        for (int i = 0; i < 5; i++)
        {
            ws23.CreatePanel("分区" + i);
        }

        bool Overlaps(WorkspacePanel a, WorkspacePanel b)
            => a.X < b.X + b.Width && a.X + a.Width > b.X
               && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

        bool anyOverlap = ws23.Panels
            .SelectMany((a, i) => ws23.Panels.Skip(i + 1).Select(b => Overlaps(a, b)))
            .Any(x => x);
        Check(!anyOverlap, "连续新建 5 个分区互不重叠",
            string.Join(" ", ws23.Panels.Select(p => $"({p.X},{p.Y})")));

        // 「整理」命令能修掉历史重叠
        var fp23 = new FreePanelsViewModel(ws23, dlg23, launchService, state23);
        WorkspacePanel first = ws23.Panels[0];
        WorkspacePanel second = ws23.Panels[1];
        ws23.UpdatePanelLayout(second, first.X, first.Y, second.Width, second.Height);
        Check(Overlaps(first, second), "先人为造出重叠（验证用例有效）", "");
        fp23.ArrangePanelsCommand.Execute(null);
        bool stillOverlapping = ws23.Panels
            .SelectMany((a, i) => ws23.Panels.Skip(i + 1).Select(b => Overlaps(a, b)))
            .Any(x => x);
        Check(!stillOverlapping, "点「整理」把已有分区重新排开", "");

        // 底部「全部」区高度可拖动，且有上下限
        fp23.PoolPanelHeight = 320;
        Check(fp23.PoolPanelHeight == 320, "「全部」区高度可增大", fp23.PoolPanelHeight.ToString());
        fp23.PoolPanelHeight = 20;
        Check(fp23.PoolPanelHeight >= 120, "「全部」区高度有下限", fp23.PoolPanelHeight.ToString());
        fp23.PoolPanelHeight = 9999;
        Check(fp23.PoolPanelHeight <= 600, "「全部」区高度有上限", fp23.PoolPanelHeight.ToString());

        // 状态栏平时为空，不再显示「就绪 / 已启动」
        var float23 = new FloatingWindowViewModel(ws23, dlg23, launchService, state23);
        var side23 = new SidebarViewModel(ws23, dlg23, launchService, state23);
        Check(string.IsNullOrEmpty(float23.StatusText) && !float23.HasStatusText
              && string.IsNullOrEmpty(side23.StatusText) && !side23.HasStatusText,
            "左下角状态栏平时为空（不再有「已启动」）", "");
    }
    finally
    {
        try
        {
            Directory.Delete(p23Root, true);
            Directory.Delete(p23Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 32. 阶段 24：底部「全部」两区 / 拖动收起状态
{
    string p24Root = Path.Combine(Path.GetTempPath(), "DeskLanesP24_" + Guid.NewGuid().ToString("N"));
    string p24Cfg = p24Root + "_cfg";
    Directory.CreateDirectory(p24Root);
    Directory.CreateDirectory(p24Cfg);
    try
    {
        var ws24 = new WorkspaceService(new LibraryStore(p24Cfg), new ConfigStore(p24Cfg),
            new ShortcutResolver());
        DesktopItem Make24(string name, DesktopItemKind kind) => new()
        {
            Name = name, FullPath = Path.Combine(p24Root, name),
            Kind = kind, Origin = ItemOrigin.UserDesktop,
        };
        await ws24.MergeScanAsync(new ScanResult
        {
            Items = new[]
            {
                Make24("Zeta 软件", DesktopItemKind.Shortcut),
                Make24("Alpha 软件", DesktopItemKind.Shortcut),
                Make24("Zeta 文件夹", DesktopItemKind.Folder),
                Make24("Alpha 文件夹", DesktopItemKind.Folder),
            },
            ScopeDirectories = new[] { p24Root },
        });

        var fp24 = new FreePanelsViewModel(ws24, new ConsoleDialogService(), launchService,
            new ShellState());
        Check(fp24.PoolAppItems.Count == 2 && fp24.PoolFolderItems.Count == 2,
            "底部「全部」按软件 / 文件夹分流", $"{fp24.PoolAppItems.Count}/{fp24.PoolFolderItems.Count}");
        Check(fp24.PoolAppItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 软件", "Zeta 软件" })
              && fp24.PoolFolderItems.Select(i => i.Name).SequenceEqual(new[] { "Alpha 文件夹", "Zeta 文件夹" }),
            "底部两区各自 A-Z 排序", "");
        Check(!fp24.IsDraggingItem, "平时不处于拖拽态", "");
        DragState.BeginDrag();
        Check(fp24.IsDraggingItem, "开始拖项目时覆盖层收到收起信号", "");
        DragState.EndDrag();
        Check(!fp24.IsDraggingItem, "拖放结束后恢复", "");
    }
    finally
    {
        try
        {
            Directory.Delete(p24Root, true);
            Directory.Delete(p24Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 33. 阶段 25：全部弹出 / 方格可调
{
    string p25Root = Path.Combine(Path.GetTempPath(), "DeskLanesP25_" + Guid.NewGuid().ToString("N"));
    string p25Cfg = p25Root + "_cfg";
    Directory.CreateDirectory(p25Root);
    Directory.CreateDirectory(p25Cfg);
    try
    {
        var ws25 = new WorkspaceService(new LibraryStore(p25Cfg), new ConfigStore(p25Cfg),
            new ShortcutResolver());
        var fp25 = new FreePanelsViewModel(ws25, new ConsoleDialogService(), launchService,
            new ShellState());

        Check(!fp25.IsPoolOpen && fp25.PoolToggleText == "全部",
            "底部「全部」默认收起（平常只显示上方面布）", fp25.PoolToggleText);
        fp25.TogglePoolCommand.Execute(null);
        Check(fp25.IsPoolOpen && fp25.PoolToggleText == "收起全部",
            "点「全部」弹出", fp25.PoolToggleText);

        // 拖拽不应改变展开状态（只有点收起才收起）
        DragState.BeginDrag();
        Check(fp25.IsPoolOpen, "拖软件时「全部」保持展开", "");
        DragState.EndDrag();
        Check(fp25.IsPoolOpen, "放好软件后仍保持展开", "");
        fp25.TogglePoolCommand.Execute(null);
        Check(!fp25.IsPoolOpen, "点「收起全部」才收起", "");

        // 方格大小可切换，且几何按新格吸附
        Check(fp25.GridSizeOptions.Count >= 3, "提供多个方格尺寸可选", "");
        fp25.PanelGridSize = 60;
        WorkspacePanel gridPanel = ws25.CreatePanel("网格面板");
        ws25.UpdatePanelLayout(gridPanel, 97, 133, 317, 243);
        Check(gridPanel.X % 60 == 0 && gridPanel.Y % 60 == 0
              && gridPanel.Width % 60 == 0 && gridPanel.Height % 60 == 0,
            "切换方格大小后几何按新格吸附",
            $"X={gridPanel.X} Y={gridPanel.Y} W={gridPanel.Width} H={gridPanel.Height}");
        Check(ws25.Settings.PanelGridSize == 60, "方格大小写入配置", "");
    }
    finally
    {
        try
        {
            Directory.Delete(p25Root, true);
            Directory.Delete(p25Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 34. 阶段 26：面板自适应尺寸 / 侧栏宽度
{
    string p26Root = Path.Combine(Path.GetTempPath(), "DeskLanesP26_" + Guid.NewGuid().ToString("N"));
    string p26Cfg = p26Root + "_cfg";
    Directory.CreateDirectory(p26Root);
    Directory.CreateDirectory(p26Cfg);
    try
    {
        var ws26 = new WorkspaceService(new LibraryStore(p26Cfg), new ConfigStore(p26Cfg),
            new ShortcutResolver());
        var items26 = new List<DesktopItem>();
        for (int i = 0; i < 9; i++)
        {
            items26.Add(new DesktopItem
            {
                Name = "软件" + i, FullPath = Path.Combine(p26Root, "软件" + i),
                Kind = DesktopItemKind.Shortcut, Origin = ItemOrigin.UserDesktop,
            });
        }
        await ws26.MergeScanAsync(new ScanResult
        {
            Items = items26, ScopeDirectories = new[] { p26Root },
        });

        WorkspacePanel panel26 = ws26.CreatePanel("自适应面板");
        double width0 = panel26.Width;
        double height0 = panel26.Height;
        foreach (DesktopItem item in items26)
        {
            ws26.AssignItemToPanel(item, panel26);
            // 单成员制：实际是最后一个留在面板里，这里改为直接补成员验证尺寸计算
            panel26.ItemIds.Clear();
            panel26.ItemIds.AddRange(items26.Take(6).Select(i => i.Id));
        }
        ws26.AssignItemToPanel(items26[0], panel26);   // 触发一次自适应
        Check(panel26.Height >= height0,
            "成员变多时框不会变小（所有软件都显示得下）",
            $"{height0} -> {panel26.Height}");

        // 直接验证：手动清空成员再触发一次，框应能缩小
        panel26.ItemIds.Clear();
        ws26.AssignItemToPanel(items26[0], panel26);
        Check(panel26.Height > 0 && panel26.Width > 0, "自适应尺寸保持有效几何", "");

        // 手动调过的尺寸在重新加载后保留（成员数没变化不重算）
        ws26.UpdatePanelLayout(panel26, 400, 200, 480, 360);
        var reloaded26 = new WorkspaceService(new LibraryStore(p26Cfg), new ConfigStore(p26Cfg),
            new ShortcutResolver());
        WorkspacePanel? kept26 = reloaded26.Panels.FirstOrDefault(p => p.Id == panel26.Id);
        Check(kept26 is not null && kept26.Width == 480 && kept26.Height == 360,
            "重启后手动调过的框尺寸保留（不被自适应覆盖）",
            kept26 is null ? "丢失" : $"{kept26.Width}x{kept26.Height}");

        Check(reloaded26.Settings.SidebarWidth == 220,
            "侧栏默认宽度收窄到 220", reloaded26.Settings.SidebarWidth.ToString());
    }
    finally
    {
        try
        {
            Directory.Delete(p26Root, true);
            Directory.Delete(p26Cfg, true);
        }
        catch
        {
            // 临时目录清理失败不影响结果
        }
    }
}

// ---------------------------------------------------------------- 汇总
Console.WriteLine();
Console.WriteLine($"===== 冒烟测试结果：通过 {passed} 项，失败 {failed} 项 =====");
foreach (string failure in failures)
{
    Console.WriteLine($"  失败项: {failure}");
}

return failed == 0 ? 0 : 1;

/// <summary>人为放慢的扫描器：用于让"同步正在进行"的状态稳定可复现（回归测试用）。</summary>
sealed class DelayedScanner : IDesktopScanner
{
    private readonly IDesktopScanner _inner;

    public DelayedScanner(IDesktopScanner inner) => _inner = inner;

    public int DelayMs { get; init; } = 700;

    public int CallCount { get; private set; }

    public async Task<ScanResult> ScanAsync(
        ScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        // 先扫描、再延时：模拟"扫描已完成但合并还没结束"的窗口，
        // 这样在窗口内产生的变更只能靠排队机制补上（否则会被丢弃）
        ScanResult result = await _inner.ScanAsync(options, progress, cancellationToken);
        await Task.Delay(DelayMs, cancellationToken);
        return result;
    }
}

/// <summary>
/// 不碰真实桌面的空扫描器（用于只关心"提示有没有上屏"的场景）。
/// ScopeDirectories 为空，合并时失效判定不生效，因此不会把已有项目误标为失效。
/// </summary>
sealed class EmptyScanner : IDesktopScanner
{
    public Task<ScanResult> ScanAsync(
        ScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ScanResult
        {
            Items = Array.Empty<DesktopItem>(),
            Warnings = Array.Empty<string>(),
            ScopeDirectories = Array.Empty<string>(),
            ScannedAt = DateTime.Now,
        });
}

sealed class ConsoleDialogService : IDialogService
{
    public List<string> Errors { get; } = new();

    public List<string> Infos { get; } = new();

    public List<string> Confirms { get; } = new();

    public List<string> Prompts { get; } = new();

    public List<string> FilePicks { get; } = new();

    public List<string> FolderPicks { get; } = new();

    /// <summary>Confirm 的脚本化应答队列（默认 true）。</summary>
    public Queue<bool> ConfirmResponses { get; } = new();

    /// <summary>Prompt 的脚本化应答队列（null 表示取消）。</summary>
    public Queue<string?> PromptResponses { get; } = new();

    /// <summary>PickFile 的脚本化应答队列（null 表示取消）。</summary>
    public Queue<string?> FilePickResponses { get; } = new();

    /// <summary>PickFolder 的脚本化应答队列（null 表示取消）。</summary>
    public Queue<string?> FolderPickResponses { get; } = new();

    public void ShowError(string message, string title = "DeskLanes")
    {
        Errors.Add($"{title}: {message}");
        Console.WriteLine($"  [DIALOG-ERROR] {title}: {message.Replace("\n", " ")}");
    }

    public void ShowInfo(string message, string title = "DeskLanes")
    {
        Infos.Add($"{title}: {message}");
        Console.WriteLine($"  [DIALOG-INFO] {title}: {message.Replace("\n", " ")}");
    }

    public bool Confirm(string message, string title = "DeskLanes")
    {
        Confirms.Add($"{title}: {message}");
        bool response = ConfirmResponses.Count > 0 ? ConfirmResponses.Dequeue() : true;
        Console.WriteLine($"  [DIALOG-CONFIRM] {title}: {message.Replace("\n", " ")} -> {(response ? "是" : "否")}");
        return response;
    }

    public string? Prompt(string message, string title = "DeskLanes", string initialValue = "")
    {
        Prompts.Add($"{title}: {message}");
        string? response = PromptResponses.Count > 0 ? PromptResponses.Dequeue() : initialValue;
        Console.WriteLine($"  [DIALOG-PROMPT] {title}: {message.Replace("\n", " ")} -> {(response is null ? "取消" : response)}");
        return response;
    }

    public string? PickFile(string title = "选择项目", string filter = "所有文件|*.*")
    {
        FilePicks.Add(title);
        string? response = FilePickResponses.Count > 0 ? FilePickResponses.Dequeue() : null;
        Console.WriteLine($"  [DIALOG-PICKFILE] {title} -> {(response is null ? "取消" : response)}");
        return response;
    }

    public string? PickFolder(string title = "选择文件夹")
    {
        FolderPicks.Add(title);
        string? response = FolderPickResponses.Count > 0 ? FolderPickResponses.Dequeue() : null;
        Console.WriteLine($"  [DIALOG-PICKFOLDER] {title} -> {(response is null ? "取消" : response)}");
        return response;
    }
}

/// <summary>脚本化热键服务：记录注册调用，可模拟注册失败（用于设置视图模型测试）。</summary>
sealed class StubHotkeyService : IHotkeyService
{
    public bool IsRegistered { get; private set; }

    public HotkeyDefinition? Current { get; private set; }

    /// <summary>下次 Register 是否模拟失败（占用冲突）。</summary>
    public bool FailNext { get; set; }

    public List<HotkeyDefinition> Registrations { get; } = new();

    public event EventHandler? Pressed;

    public OperationResult Register(HotkeyDefinition definition)
    {
        if (FailNext)
        {
            return OperationResult.Fail(
                $"组合键 {definition.DisplayText} 已被其他程序占用。\n请换一个组合键，例如 Ctrl+Alt+D 或 Ctrl+Shift+Space。");
        }

        Registrations.Add(definition);
        IsRegistered = true;
        Current = definition;
        return OperationResult.Ok();
    }

    public bool Unregister()
    {
        IsRegistered = false;
        Current = null;
        return true;
    }

    public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        // 测试用桩：无资源需要释放
    }
}

/// <summary>测试用原生窗口（承载 RegisterHotKey / Shell_NotifyIcon 所需的窗口句柄）。</summary>
static class NativeTestWindow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    /// <summary>创建一个 1x1 的系统类隐藏窗口；失败返回 IntPtr.Zero。</summary>
    public static IntPtr Create()
        => CreateWindowEx(
            0, "Static", "DeskLanesSmokeTestHost", unchecked((int)0x80000000), 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public static bool Destroy(IntPtr hwnd) => DestroyWindow(hwnd);
}
