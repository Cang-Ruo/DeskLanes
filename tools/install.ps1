#Requires -Version 5.1
<#
  install.ps1 —— DeskLanes 每用户安装脚本（不需要管理员权限）

  做什么：
    1. 选定要安装的版本（便携版 / 自包含版），找不到时会列出候选让你选
    2. 便携版才检查目标机器是否装了 .NET 10 桌面运行时，缺了会明确提示下载页
    3. 结束正在运行的 DeskLanes（否则 exe 被占用、复制不了）
    4. 复制 DeskLanes.exe 到 %LocalAppData%\Programs\DeskLanes
    5. 写「设置 → 应用 → 已安装的应用」里的卸载项（HKCU\...\Uninstall\DeskLanes），
       并把 uninstall.ps1 一起放过去，这样系统里点「卸载」就是这个脚本
    6. 开始菜单快捷方式（可选 -DesktopShortcut 加桌面快捷方式）

  两个版本（二选一，互不混淆，各自独立目录）：
    portable       约 0.5 MB，目标机必须已装 .NET 10 桌面运行时
                   来源：publish\win-x64\DeskLanes.exe
    selfcontained  约 80~100 MB，自带运行时，目标机什么都不用装
                   来源：publish\win-x64-selfcontained\DeskLanes.exe

  不做什么（与项目安全约定一致）：
    - 不碰开机自启、不隐藏桌面图标 —— 这两项都是 opt-in，由用户在软件设置里自己开
    - 不动桌面上的任何文件，不写 HKLM（不需要管理员权限），不重启资源管理器

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Edition selfcontained
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Edition portable -DesktopShortcut
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -InstallDir D:\Tools\DeskLanes -Launch
#>
param(
    [ValidateSet('auto', 'portable', 'selfcontained')]
    [string]$Edition        = 'auto',
    [string]$SourceExe      = '',
    [string]$InstallDir     = (Join-Path $env:LOCALAPPDATA 'Programs\DeskLanes'),
    [switch]$DesktopShortcut,
    [switch]$NoStartMenu,
    [switch]$Launch,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$productName = 'DeskLanes'
$productVersion = '0.1.0'
$uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskLanes'

function Say($msg) { Write-Host $msg }

Say '===== DeskLanes 安装 ====='

# ---- 1. 选定版本并定位 DeskLanes.exe ----
$repoRoot = Split-Path -Parent $scriptDir

# 通过体积判断手里这个 exe 是哪一版：自包含版 60 MB 以上，便携版不到 1 MB
function Get-ExeEdition([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -gt 20MB) { return 'selfcontained' }
    return 'portable'
}

function Resolve-EditionPath([string]$edition) {
    if ($edition -eq 'selfcontained') {
        return (Join-Path $repoRoot 'publish\win-x64-selfcontained\DeskLanes.exe')
    }
    return (Join-Path $repoRoot 'publish\win-x64\DeskLanes.exe')
}

$portablePath = Resolve-EditionPath 'portable'
$selfPath     = Resolve-EditionPath 'selfcontained'

if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    # 情况 A：install.ps1 与 exe 放在同一个目录里分发（最常见给用户的方式）
    $candidate = Join-Path $scriptDir 'DeskLanes.exe'
    if (Test-Path -LiteralPath $candidate) {
        $SourceExe = $candidate
        if ($Edition -eq 'auto') { $Edition = Get-ExeEdition $SourceExe }
    } else {
        # 情况 B：从仓库的 publish 目录里挑
        if ($Edition -eq 'auto') {
            $havePortable = Test-Path -LiteralPath $portablePath
            $haveSelf     = Test-Path -LiteralPath $selfPath
            if ($havePortable -and $haveSelf) {
                Say '检测到两个发布版本：'
                Say ('  [1] portable       约 0.5 MB  需要目标机已装 .NET 10 桌面运行时  ({0})' -f $portablePath)
                Say ('  [2] selfcontained  约 62 MB   自带运行时，目标机无需安装任何东西 ({0})' -f $selfPath)
                if ($Force) {
                    $Edition = 'portable'
                    Say ('-Force 已指定，自动选择 {0}' -f $Edition)
                } else {
                    $ans = ''
                    try { $ans = Read-Host '安装哪一个？(1/2，直接回车=1)' } catch { $ans = '1' }
                    if ($ans -eq '2') { $Edition = 'selfcontained' } else { $Edition = 'portable' }
                }
            } elseif ($havePortable) {
                $Edition = 'portable'
                Say '只找到便携版（publish\win-x64），安装它。'
            } elseif ($haveSelf) {
                $Edition = 'selfcontained'
                Say '只找到自包含版（publish\win-x64-selfcontained），安装它。'
            }
        }
        if ($Edition -ne 'auto') { $SourceExe = Resolve-EditionPath $Edition }
    }
}

if ([string]::IsNullOrWhiteSpace($SourceExe) -or -not (Test-Path -LiteralPath $SourceExe)) {
    Say '找不到 DeskLanes.exe。'
    Say '  请把 install.ps1 放在 DeskLanes.exe 同目录，或用 -SourceExe 指定路径，'
    Say ('  或先发布再装：publish\win-x64\DeskLanes.exe / {0}' -f $selfPath)
    exit 1
}
$SourceExe = (Resolve-Path -LiteralPath $SourceExe).Path
# 只要显式给了 exe，就以这个文件的实际体积为准判断版本（-SourceExe 优先级高于 -Edition）
$srcSize = (Get-Item -LiteralPath $SourceExe).Length
$detected = Get-ExeEdition $SourceExe
if ($Edition -ne 'auto' -and $Edition -ne $detected) {
    Say ('提示：-Edition 写的是 {0}，但这个 exe 有 {1} 字节，按体积判断是 {2}；以实际文件为准。' -f $Edition, $srcSize, $detected)
}
$Edition = $detected

$editionLabel = if ($Edition -eq 'selfcontained') { '自包含版（自带运行时）' } else { '便携版（依赖 .NET 10 桌面运行时）' }
Say ("安装版本  : {0}  [{1}]" -f $Edition, $editionLabel)
Say ("安装包来源: {0}" -f $SourceExe)
Say ("安装包大小: {0:N0} 字节" -f (Get-Item -LiteralPath $SourceExe).Length)
Say ("安装目录  : {0}" -f $InstallDir)

# ---- 2. 运行时检查（只有便携版需要） ----
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

if ($Edition -eq 'selfcontained') {
    Say '运行时检查: 跳过 —— 自包含版自带运行时，目标机无需安装 .NET'
} else {
    $runtime = Test-DesktopRuntime
    if ($runtime) {
        Say ("运行时检查: 已安装 Microsoft.WindowsDesktop.App {0}  ✓" -f ($runtime -join ', '))
    } else {
        Say '运行时检查: 未发现 .NET 10 桌面运行时（Microsoft.WindowsDesktop.App 10.x）。'
        Say '            DeskLanes 用 WPF，便携版必须装它才能运行（只装 .NET Runtime 不够）。'
        Say '            不想装运行时？改用自包含版：-Edition selfcontained'
        Say '            下载: https://dotnet.microsoft.com/download/dotnet/10.0  → 选择「桌面运行时」'
        if (-not $Force) {
            $ans = ''
            try { $ans = Read-Host '仍然继续安装？(y/N)' } catch { $ans = 'y' }
            if ($ans -ne 'y' -and $ans -ne 'Y') { Say '已取消。'; exit 0 }
        }
    }
}

# ---- 3. 结束正在运行的 DeskLanes ----
$running = Get-Process -Name $productName -ErrorAction SilentlyContinue
if ($running) {
    Say ('检测到 DeskLanes 正在运行（{0} 个进程），需要先结束才能覆盖 exe。' -f @($running).Count)
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
$targetExe = Join-Path $InstallDir 'DeskLanes.exe'
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
Set-ItemProperty -Path $uninstallKeyPath -Name 'DisplayName'     -Value 'DeskLanes' -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'DisplayVersion'  -Value $productVersion -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'Edition'         -Value $Edition -Type String
Set-ItemProperty -Path $uninstallKeyPath -Name 'Publisher'       -Value 'DeskLanes' -Type String
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
    New-Shortcut (Join-Path $sm 'DeskLanes.lnk') $targetExe 'DeskLanes 桌面工作空间'
    New-Shortcut (Join-Path $sm '卸载 DeskLanes.lnk') 'powershell.exe' '卸载 DeskLanes'
    $sc = $ws.CreateShortcut((Join-Path $sm '卸载 DeskLanes.lnk'))
    $sc.TargetPath = 'powershell.exe'
    $sc.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'uninstall.ps1') + '"'
    $sc.Description = '卸载 DeskLanes'
    $sc.Save()
    Say ("开始菜单快捷方式: {0}" -f $sm)
}
if ($DesktopShortcut) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeskLanes.lnk') $targetExe 'DeskLanes 桌面工作空间'
    Say '已创建桌面快捷方式'
}

# ---- 7. 完成 ----
Say ''
Say ('安装完成：{0}' -f $targetExe)
Say ('已安装版本：{0}（{1:N0} 字节）' -f $Edition, (Get-Item -LiteralPath $targetExe).Length)
Say '开机自启与桌面图标隐藏都没有被改动 —— 需要的话请在软件「设置」里自己开启。'
if ($Launch) {
    Start-Process -FilePath $targetExe
    Say '已启动 DeskLanes。'
} else {
    Say '运行方式：开始菜单 → DeskLanes，或直接执行上面的 exe。'
}
Say '===== 安装结束 ====='
