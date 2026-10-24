#Requires -Version 5.1
<#
  install.ps1 —— DeskSpace 每用户安装脚本（不需要管理员权限）

  做什么：
    1. 检查目标机器是否装了 .NET 10 桌面运行时（WPF 必需），缺了会明确提示下载页
    2. 结束正在运行的 DeskSpace（否则 exe 被占用、复制不了）
    3. 复制 DeskSpace.exe 到 %LocalAppData%\Programs\DeskSpace
    4. 写「设置 → 应用 → 已安装的应用」里的卸载项（HKCU\...\Uninstall\DeskSpace），
       并把 uninstall.ps1 一起放过去，这样系统里点「卸载」就是这个脚本
    5. 开始菜单快捷方式（可选 -DesktopShortcut 加桌面快捷方式）

  不做什么（与项目安全约定一致）：
    - 不碰开机自启、不隐藏桌面图标 —— 这两项都是 opt-in，由用户在软件设置里自己开
    - 不动桌面上的任何文件，不写 HKLM（不需要管理员权限），不重启资源管理器

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -DesktopShortcut
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -InstallDir D:\Tools\DeskSpace -Launch
#>
param(
    [string]$SourceExe    = '',
    [string]$InstallDir   = (Join-Path $env:LOCALAPPDATA 'Programs\DeskSpace'),
    [switch]$DesktopShortcut,
    [switch]$NoStartMenu,
    [switch]$Launch,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$productName = 'DeskSpace'
$productVersion = '0.1.0'
$uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskSpace'

function Say($msg) { Write-Host $msg }

Say '===== DeskSpace 安装 ====='

# ---- 1. 定位 DeskSpace.exe ----
if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $candidate = Join-Path $scriptDir 'DeskSpace.exe'
    if (Test-Path -LiteralPath $candidate) {
        $SourceExe = $candidate
    } else {
        $repoPublish = Join-Path (Split-Path -Parent $scriptDir) 'publish\win-x64\DeskSpace.exe'
        if (Test-Path -LiteralPath $repoPublish) { $SourceExe = $repoPublish }
    }
}
if ([string]::IsNullOrWhiteSpace($SourceExe) -or -not (Test-Path -LiteralPath $SourceExe)) {
    Say '找不到 DeskSpace.exe。请把 install.ps1 放在 DeskSpace.exe 同目录，或用 -SourceExe 指定路径。'
    exit 1
}
$SourceExe = (Resolve-Path -LiteralPath $SourceExe).Path
Say ("安装包来源: {0}" -f $SourceExe)
Say ("安装目录  : {0}" -f $InstallDir)

# ---- 2. 运行时检查 ----
function Test-DesktopRuntime {
    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') }
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) { $roots += (Join-Path $pf86 'dotnet\shared\Microsoft.WindowsDesktop.App') }
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Programs\dotnet\shared\Microsoft.WindowsDesktop.App') }
    if ($env:USERPROFILE) { $roots += (Join-Path $env:USERPROFILE '.dotnet\shared\Microsoft.WindowsDesktop.App') }

    foreach ($r in $roots) {
        if (Test-Path -LiteralPath $r) {
            $v = Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -like '10.*' }
            if ($v) { return ($v | ForEach-Object { $_.Name }) }
        }
    }
    return $null
}

$runtime = Test-DesktopRuntime
if ($runtime) {
    Say ("运行时检查: 已安装 Microsoft.WindowsDesktop.App {0}  ✓" -f ($runtime -join ', '))
} else {
    Say '运行时检查: 未发现 .NET 10 桌面运行时（Microsoft.WindowsDesktop.App 10.x）。'
    Say '            DeskSpace 用 WPF，必须装它才能运行（只装 .NET Runtime 不够）。'
    Say '            下载: https://dotnet.microsoft.com/download/dotnet/10.0  → 选择「桌面运行时」'
    if (-not $Force) {
        $ans = Read-Host '仍然继续安装？(y/N)'
        if ($ans -ne 'y' -and $ans -ne 'Y') { Say '已取消。'; exit 0 }
    }
}

# ---- 3. 结束正在运行的 DeskSpace ----
$running = Get-Process -Name $productName -ErrorAction SilentlyContinue
if ($running) {
    Say ('检测到 DeskSpace 正在运行（{0} 个进程），需要先结束才能覆盖 exe。' -f @($running).Count)
    if (-not $Force) {
        $ans = Read-Host '结束它们并继续安装？(y/N)'
        if ($ans -ne 'y' -and $ans -ne 'Y') { Say '已取消。'; exit 0 }
    }
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# ---- 4. 复制文件 ----
if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
$targetExe = Join-Path $InstallDir 'DeskSpace.exe'
Copy-Item -LiteralPath $SourceExe -Destination $targetExe -Force

$uninstallScript = Join-Path $scriptDir 'uninstall.ps1'
if (Test-Path -LiteralPath $uninstallScript) {
    Copy-Item -LiteralPath $uninstallScript -Destination (Join-Path $InstallDir 'uninstall.ps1') -Force
} else {
    Say ('警告: 同目录下没找到 uninstall.ps1，"已安装的应用"里的卸载按钮会失效。')
}

# ---- 5. 写卸载项 ----
$uninstallCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'uninstall.ps1') + '"'
if (-not (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')) {
    New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -Force | Out-Null
}
New-Item -Path $uninstallKeyPath -Force | Out-Null
Set-ItemProperty -Path $uninstallKeyPath -Name 'DisplayName'     -Value 'DeskSpace' -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'DisplayVersion'  -Value $productVersion -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'Publisher'       -Value 'DeskSpace' -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'DisplayIcon'     -Value $targetExe -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'InstallLocation' -Value $InstallDir -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'UninstallString' -Value $uninstallCmd -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'QuietUninstallString' -Value ($uninstallCmd + ' -Silent') -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'NoModify'  -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKeyPath -Name 'NoRepair'  -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKeyPath -Name 'InstallDate' -Value (Get-Date -Format 'yyyyMMdd') -Type String
Say '已写入卸载项（设置 → 应用 → 已安装的应用 里可卸载）'

# ---- 6. 快捷方式 ----
$ws = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path, [string]$target, [string]$desc) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $sc = $ws.CreateShortcut($path)
    $sc.TargetPath = $target
    $sc.WorkingDirectory = (Split-Path -Parent $target)
    $sc.Description = $desc
    $sc.IconLocation = $target + ',0'
    $sc.Save()
}

if (-not $NoStartMenu) {
    $sm = Join-Path $env:APPDATA ('Microsoft\Windows\Start Menu\Programs\' + $productName)
    New-Shortcut (Join-Path $sm 'DeskSpace.lnk') $targetExe 'DeskSpace 桌面工作空间'
    New-Shortcut (Join-Path $sm '卸载 DeskSpace.lnk') 'powershell.exe' '卸载 DeskSpace'
    $sc = $ws.CreateShortcut((Join-Path $sm '卸载 DeskSpace.lnk'))
    $sc.TargetPath = 'powershell.exe'
    $sc.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'uninstall.ps1') + '"'
    $sc.Description = '卸载 DeskSpace'
    $sc.Save()
    Say ("开始菜单快捷方式: {0}" -f $sm)
}
if ($DesktopShortcut) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeskSpace.lnk') $targetExe 'DeskSpace 桌面工作空间'
    Say '已创建桌面快捷方式'
}

# ---- 7. 完成 ----
Say ''
Say ('安装完成：{0}' -f $targetExe)
Say '开机自启与桌面图标隐藏都没有被改动 —— 需要的话请在软件「设置」里自己开启。'
if ($Launch) {
    Start-Process -FilePath $targetExe
    Say '已启动 DeskSpace。'
} else {
    Say '运行方式：开始菜单 → DeskSpace，或直接执行上面的 exe。'
}
Say '===== 安装结束 ====='
