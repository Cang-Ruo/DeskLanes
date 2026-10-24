// ============================================================================
// DeskSpace 冒烟测试（阶段 1-4）
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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DeskSpace.App.Services;
using DeskSpace.App.ViewModels;
using DeskSpace.Core;
using DeskSpace.Core.Models;
using DeskSpace.Core.Services;

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
    FullPath = Path.Combine(Path.GetTempPath(), "DeskSpace-不存在-" + Guid.NewGuid().ToString("N")[..8]),
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

// ---------------------------------------------------------------- 6. 异常与边界（临时目录）
Console.WriteLine();
Console.WriteLine("== 6. 异常与边界（全部使用临时目录，不碰桌面） ==");
string tempRoot = Path.Combine(Path.GetTempPath(), "DeskSpaceSmoke_" + Guid.NewGuid().ToString("N")[..8]);
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
string vmWsDir = Path.Combine(Path.GetTempPath(), "DeskSpaceVM_" + Guid.NewGuid().ToString("N")[..8]);
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
string wsRoot = Path.Combine(Path.GetTempPath(), "DeskSpaceWS_" + Guid.NewGuid().ToString("N")[..8]);
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
    Check(panel2.X == 100 && panel2.Y == 200 && panel2.Width == 400 && panel2.Height == 300,
        "分区位置与尺寸更新");

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

    // ---- 失效路径：保留配置并标记失效 ----
    string gonePath = Path.Combine(fakeUser, "会消失.txt");
    File.WriteAllText(gonePath, "gone");
    ScanResult withGone = await scanner.ScanAsync(wsOptions);
    await workspace.MergeScanAsync(withGone);
    DesktopItem goneItem = workspace.Items.First(i => i.FullPath == gonePath);
    workspace.AssignItemToGroup(goneItem, group2);

    File.Delete(gonePath);
    ScanResult afterGone = await scanner.ScanAsync(wsOptions);
    await workspace.MergeScanAsync(afterGone);
    Check(!goneItem.IsAvailable, "磁盘上消失的项目标记为失效");
    Check(workspace.Items.Contains(goneItem), "失效项目保留在库中（配置不丢）");
    Check(group2.ItemIds.Contains(goneItem.Id), "失效项目的分类关联保留");

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
    Check(workspace2.Items.Any(i => string.Equals(i.PathKey, goneItem.PathKey, StringComparison.OrdinalIgnoreCase) && !i.IsAvailable),
        "重启后失效项目仍标记失效");
    Check(workspace2.Groups.Count == workspace.Groups.Count, "重启后分类保留");
    Check(workspace2.Panels.Count == workspace.Panels.Count, "重启后分区保留");

    WorkspacePanel panel2Reloaded = workspace2.Panels.First(p => p.Id == panel2.Id);
    Check(panel2Reloaded.X == panel2.X && panel2Reloaded.Y == panel2.Y &&
          panel2Reloaded.Width == panel2.Width && panel2Reloaded.Height == panel2.Height,
        "重启后分区几何保留", $"({panel2Reloaded.X},{panel2Reloaded.Y},{panel2Reloaded.Width},{panel2Reloaded.Height})");
    Check(panel2Reloaded.ItemIds.SequenceEqual(panel2.ItemIds), "重启后分区成员关联保留");
    Check(workspace2.Groups.First(g => g.Id == group2.Id).ItemIds.Contains(goneItem.Id),
        "重启后分类成员关联保留");
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

    // 选中已有分类（group2 内有失效项目 goneItem）
    CategoryNodeViewModel group2Node = nav.Categories.First(c => c.Group?.Id == group2.Id);
    nav.SelectedCategory = group2Node;
    Check(nav.Items.Count == 1 && !nav.HasNoItems, "选中分类展示其项目",
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

    // 数据共享：自由分区中的归属调整，分类导航立即可见（同一份底层数据）
    nav.SelectedCategory = nav.Categories[0]; // 未分组
    int ungroupedBefore = nav.Items.Count;
    DesktopItem shareItem = nav.Items.First();
    fp.AssignToPanel(shareItem, panelVm);
    Check(!nav.Items.Contains(shareItem), "两种布局共用同一份数据（分区归属同步到分类导航）");
    Check(nav.Items.Count == ungroupedBefore - 1, "分类导航未分组数量同步减少");

    // Changed 事件后面板视图模型会整体重建，需重新获取最新实例验证
    PanelViewModel refreshedPanel = fp.Panels.First(p => p.Model.Id == panel2.Id);
    Check(refreshedPanel.Items.Contains(shareItem), "项目成功加入分区");

    fp.AssignToPool(shareItem);
    Check(nav.Items.Contains(shareItem) && fp.PoolItems.Contains(shareItem), "放回未分区池后两边都可见");

    fp.AssignToPanel(shareItem, refreshedPanel);
    Check(refreshedPanel.Items.Contains(shareItem) && !fp.PoolItems.Contains(shareItem), "重新拖入分区");
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
string cfgRoot = Path.Combine(Path.GetTempPath(), "DeskSpaceCfg_" + Guid.NewGuid().ToString("N")[..8]);
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

string syncRoot = Path.Combine(Path.GetTempPath(), "DeskSpaceStage3_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ---- 12.5 删除：标记失效但保留配置 ----
    File.Delete(renamedFile);
    await WaitForSyncAsync(syncService);
    DesktopItem? removed = renamed is null
        ? null
        : syncWorkspace.Items.FirstOrDefault(i => i.Id == renamed.Id);
    Check(removed is not null, "删除后项目仍保留在库中（用户配置不丢）");
    Check(removed is not null && !removed.IsAvailable, "删除后项目标记为失效");
    Check(removed is not null && keepGroup.ItemIds.Contains(removed.Id), "失效项目的分类关联保留");

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

    Check(syncWorkspace.Groups.Count == 1 && syncWorkspace.Groups[0].ItemIds.Count >= 0,
        "同步后分类仍在（不被重建）");
    Check(syncWorkspace.Panels.Count == 1 && keepPanel.X == 111 && keepPanel.Width == 333,
        "同步后分区几何保留",
        $"X={keepPanel.X} W={keepPanel.Width}");
    Check(keepPanel.ItemIds.Contains(panelTarget.Id), "同步后分区成员关系保留");
    Check(syncWorkspace.Settings.LayoutMode == LayoutMode.FreePanels, "同步后布局模式保留");
    Check(syncWorkspace.Settings.WindowWidth == 1000 && syncWorkspace.Settings.WindowLeft == 10,
        "同步后窗口偏好保留");

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
    Check(victimItem is not null && !victimItem.IsAvailable, "全量扫描后已删除的项目被正确标记失效");

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
    settingDefaults.SidebarWidth == 280,
    "侧边栏默认停靠右侧、展开、宽度 280");
Check(settingDefaults.SidebarAutoCollapse, "侧边栏默认失焦收起");
Check(settingDefaults.MinimizeToTray, "默认关闭窗口时最小化到托盘");

string s4Dir = Path.Combine(Path.GetTempPath(), "DeskSpaceS4_" + Guid.NewGuid().ToString("N")[..8]);
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
string modeDir = Path.Combine(Path.GetTempPath(), "DeskSpaceMode_" + Guid.NewGuid().ToString("N")[..8]);
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
string floatDir = Path.Combine(Path.GetTempPath(), "DeskSpaceFloat_" + Guid.NewGuid().ToString("N")[..8]);
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
string sideDir = Path.Combine(Path.GetTempPath(), "DeskSpaceSide_" + Guid.NewGuid().ToString("N")[..8]);
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
    Check(sidebar.Categories.Count == 1 && sidebar.SelectedCategory == sidebar.Categories[0], "侧边栏默认选中第一个分类");
    Check(sidebar.Items.Count == 1, "侧边栏显示当前分类的项目");
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
string setDir = Path.Combine(Path.GetTempPath(), "DeskSpaceSet_" + Guid.NewGuid().ToString("N")[..8]);
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
            using var tray = new TrayIconService(trayWindow) { ToolTipText = "DeskSpace" };
            tray.MenuItems = new List<TrayMenuItem>
            {
                new("打开工作空间", 2001),
                TrayMenuItem.Separator(),
                new("退出 DeskSpace", 2006),
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
string shareDir = Path.Combine(Path.GetTempPath(), "DeskSpaceShare_" + Guid.NewGuid().ToString("N")[..8]);
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
    Check(shareSidebar.Categories.Count == 0, "无分类时侧边栏分类为空（不伪造数据）");

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
    // 只读写当前用户 Run 键里名为 DeskSpace 的这一个值；测试结束恢复原状。
    const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string valueName = "DeskSpace";

    var svc = new DeskSpace.App.Services.StartupService();
    Check(svc.StatusText is null, "开机自启服务可读取启动项状态", svc.StatusText);
    bool original = svc.IsEnabled;
    Info($"测试前开机自启状态：{(original ? "已启用" : "未启用")}");

    OperationResult enableResult = svc.SetEnabled(true);
    Check(enableResult.Success, "可启用开机自启", enableResult.ErrorMessage);
    Check(svc.IsEnabled, "启用后状态为已启用");

    using (RegistryKey? verify = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
    {
        object? raw = verify?.GetValue(valueName);
        Check(raw is string s && !string.IsNullOrEmpty(s), "注册表 Run 键写入了 DeskSpace 启动项");
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
    var iconSvc = new DeskSpace.App.Services.DesktopIconService();
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

    public void ShowError(string message, string title = "DeskSpace")
    {
        Errors.Add($"{title}: {message}");
        Console.WriteLine($"  [DIALOG-ERROR] {title}: {message.Replace("\n", " ")}");
    }

    public void ShowInfo(string message, string title = "DeskSpace")
    {
        Infos.Add($"{title}: {message}");
        Console.WriteLine($"  [DIALOG-INFO] {title}: {message.Replace("\n", " ")}");
    }

    public bool Confirm(string message, string title = "DeskSpace")
    {
        Confirms.Add($"{title}: {message}");
        bool response = ConfirmResponses.Count > 0 ? ConfirmResponses.Dequeue() : true;
        Console.WriteLine($"  [DIALOG-CONFIRM] {title}: {message.Replace("\n", " ")} -> {(response ? "是" : "否")}");
        return response;
    }

    public string? Prompt(string message, string title = "DeskSpace", string initialValue = "")
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
            0, "Static", "DeskSpaceSmokeTestHost", unchecked((int)0x80000000), 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public static bool Destroy(IntPtr hwnd) => DestroyWindow(hwnd);
}
