// ============================================================================
// DeskLanes 阶段 3 · 真实桌面自动同步验收
// ----------------------------------------------------------------------------
// 使用真实 Known Folder 桌面路径与真实配置目录（%LocalAppData%\DeskLanes），
// 通过真实服务（DesktopScanner / WorkspaceService / DesktopSyncService）验证
// 自动发现、重命名、删除、批量创建、自定义项保护与用户配置保护。
//
// 注意：本工具会在真实桌面上创建带 "DeskLanesStage3_" 前缀的临时测试项，
//       并在结束时删除；会写入真实配置目录，运行前请先备份配置。
//       它只创建/删除自己创建的测试文件，不移动、不修改桌面上的任何既有文件。
// 用法：dotnet run --project tests/DeskLanes.RealDesktopCheck [--config=<目录>]
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.Text;
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

async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
{
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs)
    {
        if (condition())
        {
            return true;
        }

        await Task.Delay(60);
    }

    return condition();
}

Console.OutputEncoding = Encoding.UTF8;

string? userDesktop = KnownFolders.TryGetPath(KnownFolders.FOLDERID_Desktop);
string? publicDesktop = KnownFolders.TryGetPublicDesktopPath();
Check(!string.IsNullOrEmpty(userDesktop), "解析真实用户桌面路径", userDesktop);
Check(!string.IsNullOrEmpty(publicDesktop), "解析真实公共桌面路径", publicDesktop);

string configDir = args
    .FirstOrDefault(a => a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
    ?.Substring("--config=".Length)
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLanes");

Info($"配置目录：{configDir}");
Info($"用户桌面：{userDesktop}");
Info($"公共桌面：{publicDesktop}");

const string Prefix = "DeskLanesStage3_";
var testPaths = new List<string>();

string? externalFile = null;
DesktopItem? customItem = null;
IDesktopSyncService? syncService = null;

try
{
    var resolver = new ShortcutResolver();
    var scanner = new DesktopScanner(resolver);
    var workspace = new WorkspaceService(
        new LibraryStore(configDir), new ConfigStore(configDir), resolver);

    int baselineItems = workspace.Items.Count;
    int baselineGroups = workspace.Groups.Count;
    int baselinePanels = workspace.Panels.Count;
    LayoutMode baselineLayout = workspace.Settings.LayoutMode;
    double baselineWidth = workspace.Settings.WindowWidth;
    Info($"基线：项目 {baselineItems} · 分类 {baselineGroups} · 分区 {baselinePanels} · 布局 {baselineLayout}");

    // ---- 6. 桌面外自定义项（真实 AddCustomItem 路径） ----
    string externalDir = Path.Combine(Path.GetTempPath(), "DeskLanesStage3_External");
    Directory.CreateDirectory(externalDir);
    externalFile = Path.Combine(externalDir, "桌面外自定义项.txt");
    File.WriteAllText(externalFile, "stage3 custom item");

    OperationResult addResult = workspace.AddCustomItem(externalFile);
    Check(addResult.Success, "添加桌面外自定义项（真实 API）", addResult.ErrorMessage);
    customItem = workspace.Items.FirstOrDefault(i => i.FullPath == externalFile);

    // ---- 启动自动同步 ----
    var targets = new List<WatchTarget>();
    if (userDesktop is not null)
    {
        targets.Add(new WatchTarget(userDesktop, ItemOrigin.UserDesktop, "用户桌面"));
    }

    if (publicDesktop is not null)
    {
        targets.Add(new WatchTarget(publicDesktop, ItemOrigin.PublicDesktop, "公共桌面"));
    }

    var syncOptions = new SyncOptions
    {
        DebounceInterval = TimeSpan.FromMilliseconds(250),
        MaxDebounceDelay = TimeSpan.FromSeconds(1),
        EnablePeriodicRescan = false,
    };
    var logger = new FileDiagnosticLogger(Path.Combine(configDir, "logs"));

    syncService = new DesktopSyncService(workspace, scanner, targets, syncOptions, logger);
    syncService.Start();
    Check(syncService.IsWatchActive, "真实桌面目录进入监听", syncService.StatusText);
    Info($"监听目录：{string.Join(" | ", syncService.WatchedDirectories)}");

    // ---- 1. 新增快捷方式自动发现 ----
    string lnkPath = Path.Combine(userDesktop!, Prefix + "快捷方式.lnk");
    testPaths.Add(lnkPath);
    bool lnkCreated = TryCreateShortcut(lnkPath, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
    Check(lnkCreated, "（准备）在真实桌面创建测试快捷方式", lnkPath);

    bool lnkFound = await WaitForAsync(() =>
        workspace.Items.Any(i => string.Equals(i.FullPath, lnkPath, StringComparison.OrdinalIgnoreCase)));
    Check(lnkFound, "新增快捷方式被自动发现");
    DesktopItem? lnkItem = workspace.Items.FirstOrDefault(i =>
        string.Equals(i.FullPath, lnkPath, StringComparison.OrdinalIgnoreCase));
    Check(lnkItem is not null && lnkItem.Kind == DesktopItemKind.Shortcut, "新增快捷方式被识别为快捷方式类型");
    Check(lnkItem is not null && lnkItem.Origin == ItemOrigin.UserDesktop, "新增快捷方式来源标记为用户桌面");

    // ---- 2. 新增文件夹 / 普通文件自动发现 ----
    string folderPath = Path.Combine(userDesktop!, Prefix + "文件夹");
    testPaths.Add(folderPath);
    Directory.CreateDirectory(folderPath);
    bool folderFound = await WaitForAsync(() =>
        workspace.Items.Any(i => string.Equals(i.FullPath, folderPath, StringComparison.OrdinalIgnoreCase) &&
                                 i.Kind == DesktopItemKind.Folder));
    Check(folderFound, "新增文件夹被自动发现");

    string filePath = Path.Combine(userDesktop!, Prefix + "文件.txt");
    testPaths.Add(filePath);
    File.WriteAllText(filePath, "stage3");
    bool fileFound = await WaitForAsync(() =>
        workspace.Items.Any(i => string.Equals(i.FullPath, filePath, StringComparison.OrdinalIgnoreCase) &&
                                 i.Kind == DesktopItemKind.File));
    Check(fileFound, "新增普通文件被自动发现");

    // ---- 5. 快速连续创建多个项目 ----
    var burst = new List<string>();
    for (int i = 0; i < 5; i++)
    {
        string path = Path.Combine(userDesktop!, $"{Prefix}批量{i}.txt");
        burst.Add(path);
        testPaths.Add(path);
        File.WriteAllText(path, "burst");
    }

    bool burstFound = await WaitForAsync(() =>
        burst.All(p => workspace.Items.Any(i => string.Equals(i.FullPath, p, StringComparison.OrdinalIgnoreCase))),
        10000);
    Check(burstFound, "快速连续创建的 5 个项目全部被自动发现");
    int duplicatePaths = workspace.Items
        .GroupBy(i => i.PathKey, StringComparer.OrdinalIgnoreCase)
        .Count(g => g.Count() > 1);
    Check(duplicatePaths == 0, "快速连续创建不产生重复条目", $"重复 {duplicatePaths} 组");

    // ---- 3. 重命名：复用同一项目 ----
    DesktopItem? beforeRename = workspace.Items.FirstOrDefault(i =>
        string.Equals(i.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
    string renamedPath = Path.Combine(userDesktop!, Prefix + "改名后.txt");
    testPaths.Add(renamedPath);
    int countBeforeRename = workspace.Items.Count;

    File.Move(filePath, renamedPath);
    bool renameFound = await WaitForAsync(() =>
        workspace.Items.Any(i => string.Equals(i.FullPath, renamedPath, StringComparison.OrdinalIgnoreCase)));
    Check(renameFound, "重命名后的新路径出现在库中");

    DesktopItem? afterRename = workspace.Items.FirstOrDefault(i =>
        string.Equals(i.FullPath, renamedPath, StringComparison.OrdinalIgnoreCase));
    Check(beforeRename is not null && afterRename is not null && beforeRename.Id == afterRename.Id,
        "重命名复用同一个项目 Id（不产生新条目）",
        $"旧 {beforeRename?.Id} / 新 {afterRename?.Id}");
    Check(workspace.Items.Count == countBeforeRename, "重命名后项目总数不变",
        $"{workspace.Items.Count} / {countBeforeRename}");

    // ---- 4. 删除：标记失效并保留配置 ----
    File.Delete(renamedPath);
    bool unavailableMarked = await WaitForAsync(() =>
        afterRename is not null && !afterRename.IsAvailable);
    Check(unavailableMarked, "删除后项目被标记为失效");
    Check(afterRename is not null && workspace.Items.Contains(afterRename),
        "删除后项目仍保留在库中（用户配置不丢）");

    // ---- 6. 桌面外自定义项仍然存在 ----
    Check(customItem is not null && workspace.Items.Contains(customItem) && customItem.IsAvailable,
        "桌面外自定义项在多次同步后仍然存在且可用");

    // ---- 7. 用户配置不被覆盖 ----
    Check(workspace.Groups.Count == baselineGroups, "同步后分类数量不变",
        $"{workspace.Groups.Count} / {baselineGroups}");
    Check(workspace.Panels.Count == baselinePanels, "同步后分区数量不变",
        $"{workspace.Panels.Count} / {baselinePanels}");
    Check(workspace.Settings.LayoutMode == baselineLayout, "同步后布局模式不变");
    Check(workspace.Settings.WindowWidth == baselineWidth, "同步后窗口偏好不变");

    // ---- 清理桌面测试项（验证删除同步后再移除库内记录） ----
    foreach (string path in testPaths.Where(p => p != renamedPath))
    {
        SafeDelete(path);
    }

    if (customItem is not null)
    {
        workspace.RemoveItem(customItem);
    }
}
finally
{
    syncService?.Dispose();

    foreach (string path in testPaths)
    {
        SafeDelete(path);
    }

    if (externalFile is not null)
    {
        SafeDelete(externalFile);
    }
}

Console.WriteLine();
Console.WriteLine($"===== 真实桌面自动同步验收：通过 {passed} 项，失败 {failed} 项 =====");
foreach (string failure in failures)
{
    Console.WriteLine($"  失败项: {failure}");
}

return failed == 0 ? 0 : 1;

static void SafeDelete(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [WARN] 清理失败（请手动删除）：{path} -> {ex.Message}");
    }
}

static bool TryCreateShortcut(string linkPath, string targetPath)
{
    try
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            Console.WriteLine("  [WARN] 无法获取 WScript.Shell（COM 不可用）");
            return false;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.Description = "DeskLanes 阶段 3 测试快捷方式";
        shortcut.Save();
        return File.Exists(linkPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [WARN] 创建快捷方式失败：{ex.GetType().Name}：{ex.Message}");
        return false;
    }
}
