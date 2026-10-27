#Requires -Version 5.1
<#
  stage8-step3-check.ps1 —— 阶段 8 第三步：系统级真实验收（开机自启 / 桌面图标）

  覆盖用户定义的两组验收：
    A. 开机自启：启用 → 重启后真的启动 → 禁用 → 卸载清理
    B. 桌面图标隐藏状态 ×（正常退出 / 异常强杀 / Explorer 重启）的交互

  为什么是「引导 + 自动检测」而不是全自动：
    DeskSpace 的「开机自启」和「隐藏桌面图标」都只在用户点设置窗口里的保存时才应用
    （SettingsViewModel 的保存命令写注册表 + 调服务），改配置文件再重启并不会触发。
    所以脚本负责引导你点哪几个按钮，然后自动检测系统真实状态并记录成报告。

  用法（在虚拟机上跑，两段式）：
    第一阶段（不用重启）：
      powershell -NoProfile -ExecutionPolicy Bypass -File stage8-step3-check.ps1 -Phase 1
    按脚本提示重启虚拟机后，再跑：
      powershell -NoProfile -ExecutionPolicy Bypass -File stage8-step3-check.ps1 -Phase 2

  产出：脚本同目录 stage8-step3-report-phase1.txt / stage8-step3-report-phase2.txt
        （两个文件都发回来即可；Phase 1 的报告务必在重启前拷出来，避免丢失）

  前提：DeskSpace 已经用 install.ps1 装好（自包含版即可，不需要 .NET）。

  安全说明（重要）：
    D 段会重启 Windows 资源管理器。脚本用 WMI 方式启动新的 explorer（比 Start-Process
    更稳，不会被父进程回收），并轮询最多 60 秒等桌面 shell（Progman + DefView）回来；
    回不来会明确告诉你用手动方式恢复，不会把桌面弄丢。请在虚拟机里跑这一段。
#>
param(
    [ValidateSet('1', '2')]
    [string]$Phase = '1'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# 两个阶段各写各的报告，避免 Phase 2 覆盖掉 Phase 1 的文字记录
# （2026-10-27 虚拟机实测发现：同一份 stage8-step3-report.txt 被 Phase 2 覆盖，
#  Phase 1 的 A-E 场景 PASS/FAIL 文字记录丢失，只剩截图）
$reportPath = Join-Path $scriptDir ('stage8-step3-report-phase' + $Phase + '.txt')
$installDirDefault = Join-Path $env:LOCALAPPDATA 'Programs\DeskSpace'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hideIconsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskSpace'

$script:fail = 0
$script:pass = 0
$lines = New-Object System.Collections.ArrayList

function Say($m) {
    Write-Host $m
    [void]$lines.Add($m)
}
function Check($name, $cond, $detail) {
    if ($cond) {
        Say ('  [PASS] ' + $name + '   ' + $detail)
        $script:pass = $script:pass + 1
    } else {
        Say ('  [FAIL] ' + $name + '   ' + $detail)
        $script:fail = $script:fail + 1
    }
}
function Wait-User($m) {
    Write-Host ''
    Write-Host ('>>> ' + $m)
    Read-Host '    完成后按回车继续'
}
function Stop-DeskSpace {
    $p = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force }
}
function Get-RunValue {
    $v = (Get-ItemProperty -Path $runKey -Name 'DeskSpace' -ErrorAction SilentlyContinue).DeskSpace
    return $v
}
function Get-HideIcons {
    $v = (Get-ItemProperty -Path $hideIconsKey -Name 'HideIcons' -ErrorAction SilentlyContinue).HideIcons
    if ($null -eq $v) { return $null }
    return [int]$v
}

# ---------- 原生工具（窗口枚举 / 截图 / 桌面结构） ----------
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

public static class N8
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public static IntPtr FindTopVisible(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == cls && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>桌面 shell 是否完整：Progman（顶层可见）下挂 SHELLDLL_DefView。</summary>
    public static bool ShellComplete()
    {
        var pm = FindTopVisible("Progman");
        if (pm == IntPtr.Zero) { return false; }
        var dv = FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
        return dv != IntPtr.Zero;
    }

    /// <summary>把除桌面 shell 外的可见窗口全部最小化，返回被最小化的句柄（用于截图后恢复）。</summary>
    public static IntPtr[] MinimizeAllButShell()
    {
        var keep = new string[] { "Progman", "WorkerW", "SHELLDLL_DefView", "Shell_TrayWnd",
                                  "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h) || IsIconic(h)) { return true; }
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            string cs = c.ToString();
            foreach (var k in keep) { if (cs == k) { return true; } }
            if (ShowWindow(h, SW_MINIMIZE)) { list.Add(h); }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public static void RestoreWindows(IntPtr[] handles)
    {
        if (handles == null) { return; }
        foreach (var h in handles) { ShowWindow(h, SW_RESTORE); }
    }

    public static bool SaveShot(string path)
    {
        try
        {
            SetProcessDPIAware();
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            using (var bmp = new Bitmap(sw, sh))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(new Point(0, 0), new Point(0, 0), new Size(sw, sh));
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return true;
        }
        catch { return false; }
    }
}
'@ -ReferencedAssemblies System.Drawing

function Screenshot([string]$path) {
    # 先把干扰窗口最小化再拍，拍完立刻恢复，保证截图里是桌面而不是 PowerShell 窗口
    $minimized = $null
    try { $minimized = [N8]::MinimizeAllButShell() } catch { }
    Start-Sleep -Milliseconds 800
    $ok = [N8]::SaveShot($path)
    if ($minimized) {
        try { [N8]::RestoreWindows($minimized) } catch { }
    }
    if ($ok) {
        Say ('  已截图: ' + (Split-Path -Leaf $path))
        return $true
    }
    Say '  （截图失败）'
    return $false
}

function Restart-ExplorerSafely {
    <#
      重启资源管理器，返回桌面 shell 是否恢复。
      用 WMI 启动新 explorer：脱离当前进程树，不会随脚本进程退出被回收。
    #>
    Get-Process -Name explorer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    $started = $false
    try {
        $r = Invoke-WmiMethod -Class Win32_Process -Name Create -ArgumentList 'explorer.exe' -ErrorAction Stop
        if ($r -and $r.ReturnValue -eq 0) { $started = $true }
    } catch { }
    if (-not $started) {
        try { Start-Process explorer.exe; $started = $true } catch { }
    }
    if (-not $started) {
        Say '  错误：无法启动新的 explorer 进程。'
        return $false
    }
    # 轮询等 shell 回来（最多 60 秒）
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 3
        try {
            if ([N8]::ShellComplete()) { return $true }
        } catch { }
    }
    return $false
}

# 定位安装目录与 exe
$installDir = $installDirDefault
$loc = (Get-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
if ($loc) { $installDir = $loc }
$targetExe = Join-Path $installDir 'DeskSpace.exe'

Say '==================== 阶段 8 第三步 · 系统级验收 ===================='
Say ('阶段      : ' + $Phase)
Say ('时间      : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) { Say ('系统      : ' + $os.Caption + ' (Build ' + $os.BuildNumber + ')') }
Say ('架构      : ' + $env:PROCESSOR_ARCHITECTURE)
Say ('屏幕      : ' + [N8]::GetSystemMetrics(0) + ' x ' + [N8]::GetSystemMetrics(1))
Say ('安装目录  : ' + $installDir)
Say ('exe 存在  : ' + (Test-Path -LiteralPath $targetExe))

if (-not (Test-Path -LiteralPath $targetExe)) {
    Say ''
    Say '没有找到已安装的 DeskSpace.exe。请先用 install.ps1 安装（自包含版即可），再跑本脚本。'
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    exit 1
}

# ============================ 第一阶段 ============================
if ($Phase -eq '1') {

    Say ''
    Say '---------- A. 开机自启：启用 ----------'
    Say '接下来请你手动操作（脚本会检测结果）：'
    Say '  1) 右键托盘区 DeskSpace 图标 →「设置」'
    Say '  2) 勾选「开机自启」'
    Say '  3) 点「保存」'
    Say '  4) 关闭设置窗口'
    Wait-User '上面 4 步做完后按回车'
    $runVal = Get-RunValue
    Check '开机自启启用后注册表 Run 项出现' ($null -ne $runVal) ('Run\DeskSpace = ' + $(if ($null -eq $runVal) { '(不存在)' } else { $runVal }))
    Check 'Run 项指向已安装的 exe' ($runVal -eq $targetExe) ('期望 ' + $targetExe)
    Say '  （说明：Run 项由软件的 StartupService 写入；指向的 exe 必须和安装目录一致）'

    Say ''
    Say '---------- B. 桌面图标：隐藏 → 恢复 ----------'
    Say '  1) 打开设置 → 勾选「隐藏桌面图标」→ 保存 → 关闭设置窗口'
    Wait-User '隐藏图标做完后按回车（请先关闭设置窗口，截图要拍桌面）'
    $hide1 = Get-HideIcons
    Check '隐藏后注册表 HideIcons = 1' ($hide1 -eq 1) ('HideIcons = ' + $(if ($null -eq $hide1) { '(不存在)' } else { $hide1 }))
    Screenshot (Join-Path $scriptDir 'step3-hide-on.png') | Out-Null
    Say '  （step3-hide-on.png：桌面图标应该一个都不剩）'

    Say '  现在请恢复：打开设置 → 取消勾选「隐藏桌面图标」→ 保存 → 关闭设置窗口'
    Wait-User '恢复显示做完后按回车'
    $hide2 = Get-HideIcons
    Check '恢复后注册表 HideIcons = 0' ($hide2 -eq 0) ('HideIcons = ' + $(if ($null -eq $hide2) { '(不存在)' } else { $hide2 }))
    Screenshot (Join-Path $scriptDir 'step3-hide-off.png') | Out-Null
    Say '  （step3-hide-off.png：桌面图标应该全部回来）'

    Say ''
    Say '---------- C. 异常强杀不改变图标状态 ----------'
    Say '  请再次隐藏：打开设置 → 勾选「隐藏桌面图标」→ 保存 → 关闭设置窗口'
    Wait-User '再次隐藏后按回车'
    $hide3 = Get-HideIcons
    Check '再次隐藏成功' ($hide3 -eq 1) ('HideIcons = ' + $hide3)
    $errLog = Join-Path $env:LOCALAPPDATA 'DeskSpace\logs\errors.log'
    $errBefore = 0
    if (Test-Path -LiteralPath $errLog) { $errBefore = @(Get-Content -LiteralPath $errLog).Count }

    Say '  现在强杀 DeskSpace（模拟崩溃/异常终止，不等它保存任何东西）'
    Stop-DeskSpace
    Start-Sleep -Seconds 2
    $hide4 = Get-HideIcons
    Check '强杀后图标仍是隐藏状态（HideIcons = 1）' ($hide4 -eq 1) ('HideIcons = ' + $hide4)
    $errAfter = 0
    if (Test-Path -LiteralPath $errLog) { $errAfter = @(Get-Content -LiteralPath $errLog).Count }
    Check '强杀没有写新的错误日志' ($errAfter -eq $errBefore) ('errors.log 行数 ' + $errBefore + ' -> ' + $errAfter)

    Say '  重新启动 DeskSpace，看图标状态是否被保持'
    Start-Process -FilePath $targetExe
    Start-Sleep -Seconds 12
    $proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue
    Check '强杀后能重新启动' ([bool]$proc) ('进程数=' + @($proc).Count)
    $hide5 = Get-HideIcons
    Check '重启后图标仍是隐藏（设置与系统状态一致）' ($hide5 -eq 1) ('HideIcons = ' + $hide5)

    Say ''
    Say '---------- D. Explorer 重启后状态保持 ----------'
    Say '  这一步会重启 Windows 资源管理器：'
    Say '    - 桌面会闪一下再回来（正常现象）'
    Say '    - 所有打开的文件资源管理器窗口会被关闭（请先保存好里面的操作）'
    Say '  脚本用 WMI 方式启动新 explorer 并轮询等桌面 shell 回来；'
    Say '  万一 60 秒内回不来，会提示你手动恢复，不会把桌面弄丢。'
    Wait-User '确认重启资源管理器？按回车继续'
    $shellOk = Restart-ExplorerSafely
    Check 'Explorer 重启后桌面 shell 完整（Progman + DefView）' $shellOk $(if ($shellOk) { 'shell 已自动恢复' } else { 'shell 未恢复，见下方手动恢复指引' })
    $hide6 = Get-HideIcons
    Check 'Explorer 重启后图标仍是隐藏' ($hide6 -eq 1) ('HideIcons = ' + $hide6)
    Screenshot (Join-Path $scriptDir 'step3-after-explorer-restart.png') | Out-Null
    Say '  （step3-after-explorer-restart.png：图标应仍全部隐藏）'
    if (-not $shellOk) {
        Say ''
        Say '  !! 桌面 shell 没有自动恢复，请立即手动恢复：'
        Say '     Ctrl+Shift+Esc 打开任务管理器 → 文件 → 运行新任务 → 输入 explorer.exe → 确定'
        Wait-User '桌面恢复后按回车（脚本继续）'
    }
    Say '  请看任务栏右侧托盘区：DeskSpace 图标是否自动回来了？'
    Say '  （软件注册了 TaskbarCreated 消息，Explorer 重启后应自动重新注册托盘图标）'
    $trayAns = Read-Host '    托盘图标回来了吗 (y/N)'
    Check 'Explorer 重启后托盘图标自动重新注册' ($trayAns -eq 'y' -or $trayAns -eq 'Y') ('你的回答: ' + $trayAns)

    Say ''
    Say '---------- E. 恢复正常显示 ----------'
    Say '  打开设置 → 取消勾选「隐藏桌面图标」→ 保存 → 关闭设置窗口'
    Wait-User '恢复后按回车'
    $hide7 = Get-HideIcons
    Check '恢复后 HideIcons = 0' ($hide7 -eq 0) ('HideIcons = ' + $hide7)

    Say ''
    Say '==================== 第一阶段结束 ===================='
    Say ('通过 ' + $script:pass + ' 项，失败 ' + $script:fail + ' 项')
    Say ''
    Say '下一步：重启这台虚拟机，然后再跑一次本脚本，加上 -Phase 2：'
    Say '  powershell -NoProfile -ExecutionPolicy Bypass -File stage8-step3-check.ps1 -Phase 2'
    Say '第二阶段验证：重启后是否自动启动、禁用自启、卸载是否清理干净。'
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Say ('报告已写入: ' + $reportPath)
    exit $script:fail
}

# ============================ 第二阶段 ============================
Say ''
Say '---------- F. 重启后开机自启是否生效 ----------'
$runVal = Get-RunValue
Check '重启后 Run 项仍然存在' ($null -ne $runVal) ('Run\DeskSpace = ' + $(if ($null -eq $runVal) { '(不存在)' } else { $runVal }))
$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue
Check '重启后 DeskSpace 自动启动了' ([bool]$proc) ('进程数=' + @($proc).Count)
if (-not $proc) {
    Say '  （没自动启动时排查：Run 项值是否指向存在的 exe；是否被安全软件/虚拟机自启管理拦了；'
    Say '    可以在「任务管理器 → 启动应用」里看 DeskSpace 的状态）'
}

Say ''
Say '---------- G. 禁用开机自启 ----------'
Say '  打开设置 → 取消勾选「开机自启」→ 保存 → 关闭设置窗口'
Wait-User '禁用自启后按回车'
$runVal2 = Get-RunValue
Check '禁用后 Run 项被删除' ($null -eq $runVal2) ('Run\DeskSpace = ' + $(if ($null -eq $runVal2) { '(不存在)' } else { $runVal2 }))

Say ''
Say '---------- H. 卸载清理（含恢复桌面图标） ----------'
Say '  先制造一个「卸载时正隐藏着图标」的场景：'
Say '  打开设置 → 勾选「隐藏桌面图标」→ 保存 → 关闭设置窗口'
Wait-User '隐藏图标后按回车'
$hideU1 = Get-HideIcons
Check '卸载前图标处于隐藏态' ($hideU1 -eq 1) ('HideIcons = ' + $hideU1)

$uninstallScript = Join-Path $installDir 'uninstall.ps1'
if (-not (Test-Path -LiteralPath $uninstallScript)) {
    $uninstallScript = Join-Path $scriptDir 'uninstall.ps1'
}
Say ('  执行卸载: ' + $uninstallScript)
Stop-DeskSpace
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uninstallScript -Silent 2>&1 | ForEach-Object { Say ('    | ' + $_) }

Start-Sleep -Seconds 2
$hideU2 = Get-HideIcons
Check '卸载后桌面图标已恢复显示（HideIcons = 0）' ($hideU2 -eq 0) ('HideIcons = ' + $hideU2)
Check '卸载后 Run 项不存在' ($null -eq (Get-RunValue)) ('Run\DeskSpace')
Check '卸载后安装目录已删除' (-not (Test-Path -LiteralPath $installDir)) $installDir
Check '卸载后卸载项已删除' (-not (Test-Path -LiteralPath $uninstallKey)) $uninstallKey
$smDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskSpace'
Check '卸载后开始菜单目录已删除' (-not (Test-Path -LiteralPath $smDir)) $smDir
Check '配置目录按预期保留' (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'DeskSpace')) (Join-Path $env:LOCALAPPDATA 'DeskSpace')

Screenshot (Join-Path $scriptDir 'step3-after-uninstall.png') | Out-Null
Say '  （step3-after-uninstall.png：桌面图标应全部可见）'

Say ''
Say '==================== 第二阶段结束 ===================='
Say ('通过 ' + $script:pass + ' 项，失败 ' + $script:fail + ' 项')
if ($script:fail -eq 0) {
    Say '第三步验收通过：开机自启与桌面图标的系统级行为都有可复现的记录。'
} else {
    Say '存在失败项，见上方 [FAIL]。请把报告和截图一起发回来。'
}
Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
Say ('报告已写入: ' + $reportPath)
exit $script:fail
