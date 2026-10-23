# Reproduce the exact failing scenario:
#   a REAL mouse click moves focus away from DeskSpace, then the tray menu switches mode.
# Windows denies SetForegroundWindow for a non-foreground process -> Activate() silently fails.
# This checks whether the new ForceForeground path actually gets the window to the front.
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskSpace.App\bin\Debug\net10.0-windows\DeskSpace.exe"

Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;using System.Text;
namespace Fw2 {
    public static class N {
        public static List<string> Wins=new List<string>();
        public delegate bool EWP(IntPtr h,IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
        [DllImport("user32.dll")] public static extern void mouse_event(uint f,int x,int y,uint d,IntPtr e);
        public const uint WmKeyDown=0x0100, WmKeyUp=0x0101;
        public const uint TrayCallbackMessage=0x8001;
        public const int TrayIconId=0x5201;
        public const int WmRButtonUp=0x0205;
        public const byte VkDown=0x28, VkReturn=0x0D;
        public const uint MoveEv=0x0001, LeftDownEv=0x0002, LeftUpEv=0x0004, AbsEv=0x8000;
        public static string Describe(IntPtr h){var sb=new StringBuilder(256);GetClassName(h,sb,256);var tb=new StringBuilder(256);GetWindowText(h,tb,256);uint pid=0;GetWindowThreadProcessId(h,out pid);return string.Format("0x{0:X}|{1}|{2}|{3}|{4}",h.ToInt64(),sb.ToString(),tb.ToString(),IsWindowVisible(h),pid);}
    }
}
"@

function Refresh-Wins {
    [Fw2.N]::Wins.Clear()
    [void][Fw2.N]::EnumWindows({param($h,$l) try { [Fw2.N]::Wins.Add([Fw2.N]::Describe($h)) } catch {} return $true },[IntPtr]::Zero)
}
function Find-Win([string]$title,[int]$procId) {
    Refresh-Wins
    foreach($line in [Fw2.N]::Wins){
        $p=$line.Split('|')
        if($p[2] -eq $title -and ($procId -eq 0 -or [int]$p[4] -eq $procId)){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
function Find-Host([int]$procId){
    for($i=0;$i -lt 6;$i++){
        $h=Find-Win "DeskSpaceMessageHost" $procId
        if($h -ne [IntPtr]::Zero){return $h}
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}
function Find-Menu([int]$procId){
    Refresh-Wins
    foreach($line in [Fw2.N]::Wins){
        $p=$line.Split('|')
        if($p[1] -eq "#32768" -and [int]$p[4] -eq $procId -and $p[3] -eq "True"){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
function Send-Key([IntPtr]$m,[byte]$vk){
    [void][Fw2.N]::PostMessage($m,[Fw2.N]::WmKeyDown,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [void][Fw2.N]::PostMessage($m,[Fw2.N]::WmKeyUp,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}
function Invoke-Item([int]$procId,[int]$nav,[int]$attempts){
    for($a=1;$a -le $attempts;$a++){
        $hostw=Find-Host $procId
        if($hostw -eq [IntPtr]::Zero){ Write-Host ("[F2] attempt" + $a + ": no host window"); continue }
        [void][Fw2.N]::PostMessage($hostw,[Fw2.N]::TrayCallbackMessage,[IntPtr]::new([Fw2.N]::TrayIconId),[IntPtr]::new([Fw2.N]::WmRButtonUp))
        Start-Sleep -Milliseconds 500
        $m=Find-Menu $procId
        if($m -eq [IntPtr]::Zero){ Write-Host ("[F2] attempt" + $a + ": menu did not open"); continue }
        for($i=1;$i -le $nav;$i++){ Send-Key $m ([Fw2.N]::VkDown) }
        Send-Key $m ([Fw2.N]::VkReturn)
        Start-Sleep -Milliseconds 800
        if((Find-Menu $procId) -eq [IntPtr]::Zero){ return $true }
    }
    return $false
}
function Click-Desktop([int]$x,[int]$y){
    [Fw2.N]::mouse_event([Fw2.N]::MoveEv -bor [Fw2.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [Fw2.N]::mouse_event([Fw2.N]::LeftDownEv -bor [Fw2.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [Fw2.N]::mouse_event([Fw2.N]::LeftUpEv -bor [Fw2.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}
function Report([string]$label,[IntPtr]$want){
    $ok=$false
    for($t=0;$t -lt 20;$t++){
        $fg=[Fw2.N]::GetForegroundWindow()
        if($fg -eq $want){ $ok=$true; break }
        Start-Sleep -Milliseconds 150
    }
    $fg2=[Fw2.N]::GetForegroundWindow()
    if(-not $ok){
        Refresh-Wins
        $fgs="0x" + ("{0:X}" -f $fg2.ToInt64()) + "|"
        foreach($line in [Fw2.N]::Wins){ if($line.StartsWith($fgs)){ Write-Host ("[F2]   fg desc: " + $line) } }
    }
    Write-Host ("[F2] " + $label + " -> focus ok = " + $ok)
    return $ok
}

try {
    # 上一段诊断可能把窗口模式写成 Floating 并持久化，这里先恢复默认（全屏）配置，
    # 否则下一轮启动会直接进悬浮窗模式，找不到主窗口。
    $cfgDir = "C:\Users\TTjustTT\AppData\Local\DeskSpace"
    $cfgPath = Join-Path $cfgDir "settings.json"
    $cfgBackup = Join-Path $cfgDir "settings.json.diagfocus2.bak"
    if (Test-Path -LiteralPath $cfgPath) { Copy-Item -LiteralPath $cfgPath -Destination $cfgBackup -Force }
    [System.IO.File]::WriteAllText($cfgPath, "{}")
    Write-Host "[F2] settings.json reset to defaults"

    Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $vsl=[Fw2.N]::GetSystemMetrics(76)
    $vst=[Fw2.N]::GetSystemMetrics(77)
    $vsw=[Fw2.N]::GetSystemMetrics(78)
    $vsh=[Fw2.N]::GetSystemMetrics(79)
    Write-Host ("[F2] virtual screen " + $vsl + "," + $vst + " " + $vsw + "x" + $vsh)

    $proc=Start-Process -FilePath $exe -PassThru
    $pidv=$proc.Id
    $main=[IntPtr]::Zero
    for($w=0;$w -lt 25;$w++){
        Start-Sleep -Milliseconds 1000
        $main=Find-Win "DeskSpace" $pidv
        if($main -ne [IntPtr]::Zero){ Write-Host ("[F2] main up at ~" + ($w+1) + "s"); break }
    }
    if($main -eq [IntPtr]::Zero){ Write-Host "[F2] ERROR: no main window"; throw "no main" }

    # --- case A: real click steals focus, then switch to the SAME mode (full -> full)
    Write-Host "[F2] case A: click desktop, then tray menu 3 (full) while already in full"
    Click-Desktop ([int]($vsl + $vsw * 0.5)) ([int]($vst + $vsh - 140))
    $fg=[Fw2.N]::GetForegroundWindow()
    Write-Host ("[F2]   after click, fg==main ? " + ($fg -eq $main))
    $r=Invoke-Item $pidv 3 3
    Write-Host ("[F2]   menu invoke result: " + $r)
    $main=Find-Win "DeskSpace" $pidv
    [void](Report "case A" $main)

    # --- case B: real click steals focus, then switch sidebar -> full
    Write-Host "[F2] case B: click desktop, sidebar, click desktop, then tray menu 3 (full)"
    $r=Invoke-Item $pidv 4 3
    Write-Host ("[F2]   switch to sidebar: " + $r)
    Start-Sleep -Milliseconds 600
    Click-Desktop ([int]($vsl + $vsw * 0.5)) ([int]($vst + $vsh - 140))
    $r=Invoke-Item $pidv 3 3
    Write-Host ("[F2]   switch to full: " + $r)
    Start-Sleep -Milliseconds 400
    $main=Find-Win "DeskSpace" $pidv
    [void](Report "case B" $main)
} finally {
    Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "[F2] cleanup done"
}
