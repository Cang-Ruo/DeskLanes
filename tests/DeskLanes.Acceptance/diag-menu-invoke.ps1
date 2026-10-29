# 诊断探针：隔离"托盘菜单项调用"这一环节，判断失败到底是
#   (a) 测试驱动方式问题（Enter 没真正触发菜单项），还是
#   (b) DeskLanes 应用本身的缺陷（菜单项命令没切换到侧边栏）。
# 判据：发送 Enter 后若原生菜单（#32768）关闭 => 菜单项被真正调用；
#       若菜单关闭但仍无侧边栏窗口 => 应用缺陷；若菜单没关 => 驱动方式问题。
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
namespace Dg {
    public static class N {
        public static List<string> Wins = new List<string>();
        public delegate bool EWP(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb, IntPtr l);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        public const uint WmKeyDown=0x0100, WmKeyUp=0x0101;
        public const uint TrayCallbackMessage=0x8001;
        public const int TrayIconId=0x5201;
        public const int WmRButtonUp=0x0205;
        public const byte VkDown=0x28, VkReturn=0x0D, VkControl=0x11, VkMenu=0x12, VkSpace=0x20;
        public const uint KeyUp=0x0002;
        public static string Describe(IntPtr h) {
            var sb=new System.Text.StringBuilder(256); GetClassName(h,sb,256);
            var tb=new System.Text.StringBuilder(256); GetWindowText(h,tb,256);
            uint pid=0; GetWindowThreadProcessId(h,out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
        }
    }
}
"@

function Find-WindowByTitle([string]$t) {
    [Dg.N]::Wins.Clear(); [void][Dg.N]::EnumWindows({param($h,$l)[Dg.N]::Wins.Add([Dg.N]::Describe($h));$true},[IntPtr]::Zero)
    foreach ($line in [Dg.N]::Wins) { $p=$line.Split('|'); if ($p[2]-eq $t) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
    return [IntPtr]::Zero
}
function Find-MenuWindow([int]$pid) {
    [Dg.N]::Wins.Clear(); [void][Dg.N]::EnumWindows({param($h,$l)[Dg.N]::Wins.Add([Dg.N]::Describe($h));$true},[IntPtr]::Zero)
    foreach ($line in [Dg.N]::Wins) { $p=$line.Split('|'); if (($p[1]-eq "#32768")-and([int]$p[4]-eq $pid)-and($p[3]-eq "True")) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
    return [IntPtr]::Zero
}
function Find-Host([int]$pid) {
    for ($i=0;$i-lt 6;$i++) {
        [Dg.N]::Wins.Clear(); [void][Dg.N]::EnumWindows({param($h,$l)[Dg.N]::Wins.Add([Dg.N]::Describe($h));$true},[IntPtr]::Zero)
        foreach ($line in [Dg.N]::Wins) { $p=$line.Split('|'); if (($p[2]-eq "DeskLanesMessageHost")-and([int]$p[4]-eq $pid)) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}
function Send-Key([IntPtr]$m,[byte]$vk,[IntPtr]$l) {
    [void][Dg.N]::PostMessage($m,[Dg.N]::WmKeyDown,[IntPtr]::new($vk),$l)
    Start-Sleep -Milliseconds 60
    [void][Dg.N]::PostMessage($m,[Dg.N]::WmKeyUp,[IntPtr]::new($vk),$l)
    Start-Sleep -Milliseconds 60
}
function Nav-Down([IntPtr]$m,[int]$n) { for($i=1;$i-le$n;$i++){ Send-Key $m ([Dg.N]::VkDown) [IntPtr]::Zero } }

# ---------------- 配置：覆盖为默认（不删除） ----------------
$configDir=Join-Path $env:LOCALAPPDATA "DeskLanes"
$settingsFile=Join-Path $configDir "settings.json"
$backup=$null
if (Test-Path -LiteralPath $settingsFile) { $backup=Join-Path $env:TEMP ("DgBak_"+[Guid]::NewGuid().ToString("N").Substring(0,6)); Copy-Item -LiteralPath $settingsFile -Destination $backup -Force; Set-Content -LiteralPath $settingsFile -Value "{}" -Encoding UTF8 }

try {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc = Start-Process -FilePath $exe -PassThru
    $pidv = $proc.Id
    Start-Sleep -Milliseconds 2500
    $main = Find-WindowByTitle "DeskLanes"
    Write-Host ("[DIAG] 主窗口句柄={0} 进程={1}" -f $main, $pidv)

    # ---- 测试1：菜单打开可靠性 ----
    Write-Host "== 测试1：托盘菜单打开可靠性（连开 6 次）=="
    $openOk=0
    for ($k=1;$k-le 6;$k++) {
        $hostw=Find-Host $pidv
        if ($hostw -eq [IntPtr]::Zero) { Write-Host ("  第{0}次：找不到宿主窗口" -f $k); continue }
        [void][Dg.N]::PostMessage($hostw,[Dg.N]::TrayCallbackMessage,[IntPtr]::new([Dg.N]::TrayIconId),[IntPtr]::new([Dg.N]::WmRButtonUp))
        Start-Sleep -Milliseconds 500
        $m=Find-MenuWindow $pidv
        if ($m -ne [IntPtr]::Zero) { $openOk = $openOk + 1; Write-Host ("  第{0}次：菜单打开 句柄={1}" -f $k,$m) }
        else { Write-Host ("  第{0}次：菜单未打开" -f $k) }
        # 关闭菜单
        [Dg.N]::keybd_event([Dg.N]::VkEscape,0,0,[IntPtr]::Zero); [Dg.N]::keybd_event([Dg.N]::VkEscape,0,2,[IntPtr]::Zero)
        Start-Sleep -Milliseconds 400
    }
    Write-Host ("[DIAG] 菜单打开成功 {0}/6" -f $openOk)

    # ---- 测试2：侧边栏项（第4项）调用的三种方式 ----
    Write-Host "== 测试2：调用侧边栏菜单项（index=4）=="
    $hostw=Find-Host $pidv
    [void][Dg.N]::PostMessage($hostw,[Dg.N]::TrayCallbackMessage,[IntPtr]::new([Dg.N]::TrayIconId),[IntPtr]::new([Dg.N]::WmRButtonUp))
    Start-Sleep -Milliseconds 500
    $m=Find-MenuWindow $pidv
    if ($m -eq [IntPtr]::Zero) { Write-Host "[DIAG] 菜单未打开，无法测试调用" }
    else {
        Nav-Down $m 4
        Start-Sleep -Milliseconds 200
        $stillOpen = ([Dg.N]::IsWindowVisible($m) -and (Find-MenuWindow $pidv) -ne [IntPtr]::Zero)
        Write-Host ("[DIAG] 导航4次↓后菜单仍开={0}" -f $stillOpen)

        # 方法A：PostMessage Enter, lParam=0（当前脚本方式）
        Send-Key $m ([Dg.N]::VkReturn) [IntPtr]::Zero
        Start-Sleep -Milliseconds 1000
        $closedA = ((Find-MenuWindow $pidv) -eq [IntPtr]::Zero)
        $sideA = (Find-WindowByTitle "DeskLanes 侧边栏") -ne [IntPtr]::Zero
        Write-Host ("[DIAG] 方法A(PostMessage Enter lParam0): 菜单关闭={0} 侧边栏出现={1}" -f $closedA,$sideA)
        if (-not $sideA) {
            # 重置：若菜单还在则关掉，重新打开
            [Dg.N]::keybd_event([Dg.N]::VkEscape,0,0,[IntPtr]::Zero); [Dg.N]::keybd_event([Dg.N]::VkEscape,0,2,[IntPtr]::Zero)
            Start-Sleep -Milliseconds 400
            $hostw=Find-Host $pidv
            [void][Dg.N]::PostMessage($hostw,[Dg.N]::TrayCallbackMessage,[IntPtr]::new([Dg.N]::TrayIconId),[IntPtr]::new([Dg.N]::WmRButtonUp))
            Start-Sleep -Milliseconds 500
            $m=Find-MenuWindow $pidv
            if ($m -ne [IntPtr]::Zero) {
                Nav-Down $m 4; Start-Sleep -Milliseconds 200
                # 方法B：PostMessage Enter, lParam 带扫描码 0x1C
                Send-Key $m ([Dg.N]::VkReturn) ([IntPtr]::new(0x001C0000))
                Start-Sleep -Milliseconds 1000
                $closedB = ((Find-MenuWindow $pidv) -eq [IntPtr]::Zero)
                $sideB = (Find-WindowByTitle "DeskLanes 侧边栏") -ne [IntPtr]::Zero
                Write-Host ("[DIAG] 方法B(PostMessage Enter 扫描码): 菜单关闭={0} 侧边栏出现={1}" -f $closedB,$sideB)
            }
            if (-not $sideB) {
                [Dg.N]::keybd_event([Dg.N]::VkEscape,0,0,[IntPtr]::Zero); [Dg.N]::keybd_event([Dg.N]::VkEscape,0,2,[IntPtr]::Zero)
                Start-Sleep -Milliseconds 400
                $hostw=Find-Host $pidv
                [void][Dg.N]::PostMessage($hostw,[Dg.N]::TrayCallbackMessage,[IntPtr]::new([Dg.N]::TrayIconId),[IntPtr]::new([Dg.N]::WmRButtonUp))
                Start-Sleep -Milliseconds 500
                $m=Find-MenuWindow $pidv
                if ($m -ne [IntPtr]::Zero) {
                    Nav-Down $m 4; Start-Sleep -Milliseconds 200
                    # 方法C：SetForegroundWindow(menu) + keybd_event Enter
                    [void][Dg.N]::SetForegroundWindow($m)
                    Start-Sleep -Milliseconds 100
                    [Dg.N]::keybd_event([Dg.N]::VkReturn,0,0,[IntPtr]::Zero)
                    [Dg.N]::keybd_event([Dg.N]::VkReturn,0,2,[IntPtr]::Zero)
                    Start-Sleep -Milliseconds 1000
                    $closedC = ((Find-MenuWindow $pidv) -eq [IntPtr]::Zero)
                    $sideC = (Find-WindowByTitle "DeskLanes 侧边栏") -ne [IntPtr]::Zero
                    Write-Host ("[DIAG] 方法C(SetForeground+keybd Enter): 菜单关闭={0} 侧边栏出现={1}" -f $closedC,$sideC)
                }
            }
        }
        # 收尾：如果侧边栏出来了，切回全屏避免影响后续；直接关菜单/结束进程即可
        [Dg.N]::keybd_event([Dg.N]::VkEscape,0,0,[IntPtr]::Zero); [Dg.N]::keybd_event([Dg.N]::VkEscape,0,2,[IntPtr]::Zero)
    }

    # ---- 测试3：全局快捷键 Ctrl+Alt+Space 呼出悬浮窗 ----
    Write-Host "== 测试3：Ctrl+Alt+Space 呼出悬浮窗 =="
    Start-Sleep -Milliseconds 500
    [Dg.N]::keybd_event([Dg.N]::VkControl,0,0,[IntPtr]::Zero)
    [Dg.N]::keybd_event([Dg.N]::VkMenu,0,0,[IntPtr]::Zero)
    [Dg.N]::keybd_event([Dg.N]::VkSpace,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Dg.N]::keybd_event([Dg.N]::VkSpace,0,2,[IntPtr]::Zero)
    [Dg.N]::keybd_event([Dg.N]::VkMenu,0,2,[IntPtr]::Zero)
    [Dg.N]::keybd_event([Dg.N]::VkControl,0,2,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    $float = Find-WindowByTitle "DeskLanes 快速启动"
    Write-Host ("[DIAG] 悬浮窗句柄={0} (非零=呼出成功)" -f $float)
} finally {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    if ($backup -and (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $backup -Destination $settingsFile -Force }
    Write-Host "[DIAG] 清理完成（临时备份保留在 TEMP，可手动删除）"
}

