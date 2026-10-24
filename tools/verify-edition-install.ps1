#Requires -Version 5.1
<#
  verify-edition-install.ps1 —— 阶段 8 第一步验收：两种发布版本并存

  验证点：
    A. 两条发布路径互不干扰（各自独立目录，便携版产物 MD5 不变）
    B. 自包含版能安装、能启动、能卸载
    C. 便携版能安装、能启动、能卸载
    D. -Edition 明确指定时不会串到另一条路径
    E. 不带 -Edition 时脚本会列出候选再决定（非交互环境自动回落）
    F. -SourceExe 显式指定时优先级最高
#>
param(
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'
$repo      = 'C:\Users\TTjustTT\Documents\Github\DeskSpace'
$tools     = Join-Path $repo 'tools'
$install   = Join-Path $tools 'install.ps1'
$uninstall = Join-Path $tools 'uninstall.ps1'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DeskSpace'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskSpace'
$startMenu  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeskSpace'
$targetExe  = Join-Path $installDir 'DeskSpace.exe'

$portableSrc = Join-Path $repo 'publish\win-x64\DeskSpace.exe'
$selfSrc     = Join-Path $repo 'publish\win-x64-selfcontained\DeskSpace.exe'

# 便携版基线 MD5（阶段 7 修复后的产物，本阶段不应被改动）
$portableBaselineMd5 = '59fe5240526a23de914facfdf76e92eb'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Wz {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
}
'@

$script:fail = 0
function Check($name, $cond, $detail) {
    if ($cond) { Write-Host ('  [PASS] ' + $name + '   ' + $detail) }
    else { Write-Host ('  [FAIL] ' + $name + '   ' + $detail); $script:fail = $script:fail + 1 }
}
function Md5($p) { return (Get-FileHash -LiteralPath $p -Algorithm MD5).Hash.ToLower() }
function Say($m) { Write-Host $m }

function Run-Ps([string]$file, [string[]]$argList) {
    if ($argList -and $argList.Count -gt 0) {
        return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $file @argList)
    }
    return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $file)
}

# 需要回答交互提问时用：把答案喂给子进程的 stdin（Read-Host 会读它）
function Run-PsInput([string]$file, [string[]]$argList, [string]$answer) {
    $allArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $file) + $argList
    return ($answer | & powershell.exe @allArgs)
}

function Stop-DeskSpace {
    $p = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force; Start-Sleep -Seconds 1 }
}

function Ensure-Clean {
    Stop-DeskSpace
    if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
    if (Test-Path -LiteralPath $installDir)   { Remove-Item -LiteralPath $installDir -Recurse -Force }
}

function Assert-Launches([string]$label, [bool]$expectOwnRuntime) {
    # 先清掉上一次的解压目录，避免拿旧证据当新证据
    $extractRoot = Join-Path $env:TEMP '.net\DeskSpace'
    if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }

    $null = Start-Process -FilePath $targetExe -PassThru
    Start-Sleep -Seconds 15
    $procs = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue
    Check ($label + ' 启动 15 秒后进程仍在（没启动即崩）') ([bool]$procs) ('进程数=' + (@($procs).Count))

    # 窗口判据必须用 EnumWindows：WPF 程序的 Process.MainWindowHandle 常返回 0（实测就是 0），
    # 但窗口其实是有的（标题「DeskSpace 快速启动」）
    $script:titles = @()
    foreach ($proc in $procs) {
        $script:targetPid = [uint32]$proc.Id
        $cb = [Wz+EnumWindowsProc]{
            param($h, $l)
            $p2 = 0
            $null = [Wz]::GetWindowThreadProcessId($h, [ref]$p2)
            if ($p2 -eq $script:targetPid -and [Wz]::IsWindowVisible($h)) {
                $len = [Wz]::GetWindowTextLength($h)
                $sb = New-Object System.Text.StringBuilder ($len + 1)
                $null = [Wz]::GetWindowText($h, $sb, $sb.Capacity)
                $script:titles += $sb.ToString()
            }
            return $true
        }
        $null = [Wz]::EnumWindows($cb, [IntPtr]::Zero)
    }
    Check ($label + ' 有可见窗口') (@($script:titles).Count -gt 0) ('窗口标题: ' + ($script:titles -join ' / '))

    # 自带运行时的硬证据：自包含版会把 WPF 原生组件（wpfgfx_cor3.dll 等）解压到
    # %TEMP%\.net\DeskSpace\<随机目录>；便携版用系统已装的 WPF，这里一个文件都不会解压。
    # 注意 coreclr.dll 并不落盘（由宿主从 bundle 内加载），所以拿 wpfgfx_cor3.dll 当判据。
    $wpf = Get-ChildItem -Path $extractRoot -Filter 'wpfgfx_cor3.dll' -Recurse -ErrorAction SilentlyContinue
    $hasWpf = [bool]$wpf
    $where = ''
    if ($hasWpf) { $where = ($wpf | Select-Object -First 1).FullName }
    if ($expectOwnRuntime) {
        Check '自包含版解压出 WPF 原生组件（自带运行时的证据）' $hasWpf $where
    } else {
        Check '便携版不解压任何原生组件（用系统运行时）' (-not $hasWpf) ('扫描 ' + $extractRoot)
    }
    Stop-DeskSpace
}

Say '==================== 阶段 8 第一步验收 ===================='

# ---------- A. 两条发布路径互不干扰 ----------
Say ''
Say '--- A. 两条发布路径互不干扰 ---'
$env:DOTNET_ROOT = 'C:\Users\TTjustTT\.dotnet'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$proj = Join-Path $repo 'src\DeskSpace.App\DeskSpace.App.csproj'

# A1 便携版现状（阶段 7 的 MD5 只作参考：只要重新编译过，exe 就会变，不能当判据）
$portableMd5Before = Md5 $portableSrc
$portableSize = (Get-Item -LiteralPath $portableSrc).Length
Say ('  参考：阶段 7 基线 MD5 ' + $portableBaselineMd5 + '，当前 ' + $portableMd5Before)

# A2 发布自包含版
$pub = & $dotnet publish $proj -c Release -p:PublishProfile=win-x64-selfcontained 2>&1 | Out-String
Check '自包含版发布成功' ($pub -match 'DeskSpace.App ->') 'dotnet publish 输出含产物行'

# A3 回头发便携版：产物不应被自包含发布改动（这才是"两条路径不混淆"的判据）
$pub2 = & $dotnet publish $proj -c Release -p:PublishProfile=win-x64-singlefile 2>&1 | Out-String
Check '便携版发布成功' ($pub2 -match 'DeskSpace.App ->') 'dotnet publish 输出含产物行'
$portableMd5After = Md5 $portableSrc
Check '自包含发布不影响便携版产物（MD5 不变）' ($portableMd5Before -eq $portableMd5After) ($portableMd5Before + ' -> ' + $portableMd5After)
$portableSize2 = (Get-Item -LiteralPath $portableSrc).Length
Check '便携版体积未变（约 0.5 MB 级别）' ($portableSize2 -eq $portableSize) ($portableSize2.ToString() + ' 字节')
Check '便携版仍输出到 publish\win-x64' (Test-Path -LiteralPath $portableSrc) $portableSrc
Check 'publish\win-x64 目录里只有一个 exe' ((Get-ChildItem -LiteralPath (Join-Path $repo 'publish\win-x64')).Count -eq 1) ((Get-ChildItem -LiteralPath (Join-Path $repo 'publish\win-x64')).Count.ToString() + ' 个文件')

# A4 同一份编译产物重复发布应当稳定
& $dotnet publish $proj -c Release -p:PublishProfile=win-x64-singlefile 2>&1 | Out-Null
Check '便携版重复发布 MD5 稳定' ((Md5 $portableSrc) -eq $portableMd5After) $portableMd5After

# A5 自包含版自身
$selfSize = (Get-Item -LiteralPath $selfSrc).Length
$selfMd5  = Md5 $selfSrc
Check '自包含版输出到独立目录 publish\win-x64-selfcontained' (Test-Path -LiteralPath $selfSrc) ('大小 ' + $selfSize + ' 字节 MD5 ' + $selfMd5)
Check '自包含版体积远大于便携版（自带运行时的体现）' ($selfSize -gt 20MB) ('自包含 ' + $selfSize + ' vs 便携 ' + $portableSize2)
Check 'publish\win-x64-selfcontained 目录里只有一个 exe' ((Get-ChildItem -LiteralPath (Join-Path $repo 'publish\win-x64-selfcontained')).Count -eq 1) ((Get-ChildItem -LiteralPath (Join-Path $repo 'publish\win-x64-selfcontained')).Count.ToString() + ' 个文件')
Check '两个版本输出到不同目录' ((Resolve-Path -LiteralPath (Join-Path $repo 'publish\win-x64')).Path -ne (Resolve-Path -LiteralPath (Join-Path $repo 'publish\win-x64-selfcontained')).Path) 'win-x64 / win-x64-selfcontained'

# ---------- B. 自包含版 安装 / 启动 / 卸载 ----------
Say ''
Say '--- B. 自包含版：安装 → 启动 → 卸载 ---'
Ensure-Clean
$out = Run-Ps $install @('-Edition', 'selfcontained', '-Force')
$out | ForEach-Object { Write-Host ('    | ' + $_) }
Check '自包含版安装成功（exe 落盘）' (Test-Path -LiteralPath $targetExe) $targetExe
if (Test-Path -LiteralPath $targetExe) {
    $tSize = (Get-Item -LiteralPath $targetExe).Length
    $tMd5  = Md5 $targetExe
    Check '安装的是自包含版（大小一致）' ($tSize -eq $selfSize) ($tSize.ToString() + ' / 源 ' + $selfSize)
    Check '安装的是自包含版（MD5 一致）' ($tMd5 -eq $selfMd5) $tMd5
    Check '安装输出标注为 selfcontained' (($out -join ' ') -match 'selfcontained') '输出含 selfcontained'
}
$ed = ''
if (Test-Path -LiteralPath $uninstallKey) {
    $ed = (Get-ItemProperty -LiteralPath $uninstallKey -Name 'Edition' -ErrorAction SilentlyContinue).Edition
}
Check '卸载项记录了版本 Edition=selfcontained' ($ed -eq 'selfcontained') ('Edition=' + $ed)
Check '开始菜单快捷方式已创建' (Test-Path -LiteralPath (Join-Path $startMenu 'DeskSpace.lnk')) $startMenu
Assert-Launches '自包含版' $true

Run-Ps $uninstall @('-Silent') | ForEach-Object { Write-Host ('    | ' + $_) }
Check '自包含版卸载后安装目录已删除' (-not (Test-Path -LiteralPath $installDir)) $installDir
Check '自包含版卸载后卸载项已删除' (-not (Test-Path -LiteralPath $uninstallKey)) $uninstallKey
Check '自包含版卸载后开始菜单目录已删除' (-not (Test-Path -LiteralPath $startMenu)) $startMenu
Check '配置目录默认保留' (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'DeskSpace')) (Join-Path $env:LOCALAPPDATA 'DeskSpace')

# ---------- C. 便携版 安装 / 启动 / 卸载 ----------
Say ''
Say '--- C. 便携版：安装 → 启动 → 卸载 ---'
Ensure-Clean
$out = Run-Ps $install @('-Edition', 'portable', '-Force')
$out | ForEach-Object { Write-Host ('    | ' + $_) }
Check '便携版安装成功（exe 落盘）' (Test-Path -LiteralPath $targetExe) $targetExe
if (Test-Path -LiteralPath $targetExe) {
    $tSize = (Get-Item -LiteralPath $targetExe).Length
    $tMd5  = Md5 $targetExe
    Check '安装的是便携版（大小一致）' ($tSize -eq $portableSize2) ($tSize.ToString() + ' / 源 ' + $portableSize2)
    Check '安装的是便携版（MD5 一致）' ($tMd5 -eq $portableMd5After) $tMd5
}
$ed = ''
if (Test-Path -LiteralPath $uninstallKey) {
    $ed = (Get-ItemProperty -LiteralPath $uninstallKey -Name 'Edition' -ErrorAction SilentlyContinue).Edition
}
Check '卸载项记录了版本 Edition=portable' ($ed -eq 'portable') ('Edition=' + $ed)
Assert-Launches '便携版' $false

Run-Ps $uninstall @('-Silent') | ForEach-Object { Write-Host ('    | ' + $_) }
Check '便携版卸载后安装目录已删除' (-not (Test-Path -LiteralPath $installDir)) $installDir

# ---------- D. 不带 -Edition：列出候选，按用户输入决定 ----------
Say ''
Say '--- D. 不带 -Edition：脚本列出候选并按输入选择（输入 1） ---'
Ensure-Clean
$out = Run-PsInput $install @() '1'
$out | ForEach-Object { Write-Host ('    | ' + $_) }
$joined = $out -join "`n"
Check '列出了两个版本供选择' ($joined -match '检测到两个发布版本') '输出含候选列表'
Check '候选里含 portable 体积说明' ($joined -match 'portable') '输出含 portable'
Check '候选里含 selfcontained 体积说明' ($joined -match 'selfcontained') '输出含 selfcontained'
$landed = Test-Path -LiteralPath $targetExe
$landedSize = 0
if ($landed) { $landedSize = (Get-Item -LiteralPath $targetExe).Length }
Check '输入 1 → 装的是便携版' ($landed -and ($landedSize -eq $portableSize2)) ('落盘大小 ' + $landedSize)

Say ''
Say '--- D2. 同样场景输入 2，应当选自包含版 ---'
Ensure-Clean
$out = Run-PsInput $install @() '2'
$out | ForEach-Object { Write-Host ('    | ' + $_) }
$landed2 = Test-Path -LiteralPath $targetExe
$landedSize2 = 0
if ($landed2) { $landedSize2 = (Get-Item -LiteralPath $targetExe).Length }
Check '输入 2 → 装的是自包含版' ($landed2 -and ($landedSize2 -eq $selfSize)) ('落盘大小 ' + $landedSize2)

# ---------- F. -SourceExe 优先级最高 ----------
Say ''
Say '--- E. -SourceExe 显式指定时优先级最高（不理会 -Edition） ---'
Ensure-Clean
$out = Run-Ps $install @('-SourceExe', $selfSrc, '-Edition', 'portable', '-Force')
$out | ForEach-Object { Write-Host ('    | ' + $_) }
$gotSelf = Test-Path -LiteralPath $targetExe
$tSize = 0
if ($gotSelf) { $tSize = (Get-Item -LiteralPath $targetExe).Length }
Check '以 -SourceExe 为准装了自包含版' ($gotSelf -and ($tSize -eq $selfSize)) ($tSize.ToString() + ' 字节 / 源 ' + $selfSize)
Check '版本按实际文件识别为 selfcontained' (($out -join ' ') -match '安装版本\s*:\s*selfcontained') '输出里安装版本=selfcontained（不是被 -Edition 带偏）'
Check '自包含版运行时检查被跳过' (($out -join ' ') -match '运行时检查:\s*跳过') '输出含「运行时检查: 跳过」'

if (-not $KeepInstalled) {
    Run-Ps $uninstall @('-Silent') | Out-Null
    Ensure-Clean
}

Say ''
Say ('==================== 失败项: ' + $script:fail + ' ====================')
if ($script:fail -eq 0) { Say '第一步验收通过：两种版本可独立发布、独立安装、独立卸载。' }
else { Say '存在失败项，见上方 [FAIL]。' }
exit $script:fail
