#Requires -Version 5.1
<#
  uninstall.ps1 —— DeskSpace 卸载脚本（每用户，不需要管理员权限）

  做什么：
    1. 结束正在运行的 DeskSpace（含系统托盘图标）
    2. 删除开机自启项（HKCU\...\Run\DeskSpace）—— 否则删了 exe 会留下一个指向空路径的启动项
    3. 恢复桌面图标显示（仅当当前是隐藏状态时；用系统右键菜单同款的 WM_COMMAND 0x7402）
    4. 删除开始菜单 / 桌面快捷方式
    5. 删除「已安装的应用」里的卸载项
    6. 删除安装目录（程序文件）
    7. 配置目录 %LocalAppData%\DeskSpace：默认保留（用户的分类与布局），-RemoveConfig 才删

  不做什么：
    - 不动桌面上的任何文件；不写 HKLM；不重启资源管理器

  用法（普通）：
    powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1
  用法（静默，系统"卸载"按钮走这条）：
    powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -Silent
  连配置一起删：
    powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveConfig
#>
param(
    [string]$InstallDir = '',
    [switch]$RemoveConfig,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$productName = 'DeskSpace'
$uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskSpace'

function Say($msg) { Write-Host $msg }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class UN
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public static IntPtr FindTopWindowByClass(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindDesktopDefView()
    {
        IntPtr progman = FindTopWindowByClass("Progman");
        if (progman != IntPtr.Zero)
        {
            IntPtr dv = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) { return dv; }
        }
        return IntPtr.Zero;
    }

    public static void ToggleDesktopIcons(IntPtr defView)
    {
        SendMessage(defView, 0x0111, new IntPtr(0x7402), IntPtr.Zero);
    }
}
'@

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-HideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return 0 }
    return [int]$v.HideIcons
}

Say '===== DeskSpace 卸载 ====='

# ---- 0. 定位安装目录 ----
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    if (Test-Path -LiteralPath $uninstallKeyPath) {
        $loc = (Get-ItemProperty -Path $uninstallKeyPath -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
        if ($loc -and (Test-Path -LiteralPath $loc)) { $InstallDir = $loc }
    }
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $default = Join-Path $env:LOCALAPPDATA ('Programs\' + $productName)
    if (Test-Path -LiteralPath $default) { $InstallDir = $default }
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    Say '找不到安装目录（注册表里没有安装记录，默认位置也不存在）。'
    Say '如果 DeskSpace 是绿色版/自建目录，请用 -InstallDir 指定后重试。'
    exit 1
}
Say ("安装目录: {0}" -f $InstallDir)

if (-not $Silent) {
    $ans = Read-Host ('确认卸载 DeskSpace？会删除该目录里的程序文件 (y/N)')
    if ($ans -ne 'y' -and $ans -ne 'Y') { Say '已取消。'; exit 0 }
}

# ---- 1. 结束进程 ----
$running = Get-Process -Name $productName -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Say ('已结束 DeskSpace 进程（{0} 个）' -f @($running).Count)
} else {
    Say 'DeskSpace 未在运行'
}

# ---- 2. 删除开机自启项 ----
$runVal = Get-ItemProperty -Path $runKey -Name $productName -ErrorAction SilentlyContinue
if ($null -ne $runVal) {
    Remove-ItemProperty -Path $runKey -Name $productName -ErrorAction SilentlyContinue
    Say '已删除开机自启项（HKCU Run\DeskSpace）'
} else {
    Say '开机自启项：本来就没有'
}

# ---- 3. 恢复桌面图标显示 ----
$hidden = Get-HideIcons
if ($hidden -ne 0) {
    $dv = [UN]::FindDesktopDefView()
    if ($dv -ne [IntPtr]::Zero) {
        [UN]::ToggleDesktopIcons($dv)
        Start-Sleep -Milliseconds 1200
        if ((Get-HideIcons) -eq 0) {
            Say '已恢复桌面图标显示'
        } else {
            Say '注意：已发送恢复命令，但系统状态仍未变回显示，请手动检查「查看 → 显示桌面图标」。'
        }
    } else {
        Say '注意：找不到桌面图标窗口，无法自动恢复显示。请手动用桌面右键菜单「查看 → 显示桌面图标」。'
    }
} else {
    Say '桌面图标：当前就是显示状态，无需恢复'
}

# ---- 4. 删除快捷方式 ----
$sm = Join-Path $env:APPDATA ('Microsoft\Windows\Start Menu\Programs\' + $productName)
if (Test-Path -LiteralPath $sm) {
    Remove-Item -LiteralPath $sm -Recurse -Force -ErrorAction SilentlyContinue
    Say ('已删除开始菜单目录: {0}' -f $sm)
}
$deskLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) ($productName + '.lnk')
if (Test-Path -LiteralPath $deskLnk) {
    Remove-Item -LiteralPath $deskLnk -Force -ErrorAction SilentlyContinue
    Say '已删除桌面快捷方式'
}

# ---- 5. 删除卸载项 ----
if (Test-Path -LiteralPath $uninstallKeyPath) {
    Remove-Item -Path $uninstallKeyPath -Recurse -Force -ErrorAction SilentlyContinue
    Say '已删除「已安装的应用」里的卸载项'
}

# ---- 6. 删除程序文件 ----
if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $InstallDir) {
        Say ('警告：安装目录未能完全删除（可能有文件被占用）: {0}' -f $InstallDir)
    } else {
        Say ('已删除安装目录: {0}' -f $InstallDir)
    }
}

# ---- 7. 配置目录 ----
$configDir = Join-Path $env:LOCALAPPDATA $productName
if ($RemoveConfig) {
    if (Test-Path -LiteralPath $configDir) {
        Remove-Item -LiteralPath $configDir -Recurse -Force -ErrorAction SilentlyContinue
        Say ('已删除配置目录: {0}' -f $configDir)
    }
} elseif (Test-Path -LiteralPath $configDir) {
    Say ('配置目录已保留（分类与布局都在这里）: {0}' -f $configDir)
    Say '                想彻底清除请手动删除，或用 uninstall.ps1 -RemoveConfig 重跑。'
}

Say ''
Say '===== 卸载完成 ====='
Say 'DeskSpace 已从这台机器移除。桌面上的文件从未被改动过。'
