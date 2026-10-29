# Fast repro for acceptance section 7: sidebar should auto-collapse when it loses focus.
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"

Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;using System.Text;
namespace Fs {
    public static class N {
        public static List<string> Wins=new List<string>();
        public delegate bool EWP(IntPtr h,IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
        [DllImport("user32.dll")] public static extern void mouse_event(uint f,int x,int y,uint d,IntPtr e);
        public struct RECT { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
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
    [Fs.N]::Wins.Clear()
    [void][Fs.N]::EnumWindows({param($h,$l) try { [Fs.N]::Wins.Add([Fs.N]::Describe($h)) } catch {} return $true },[IntPtr]::Zero)
}
function Find-Win([string]$title,[int]$procId) {
    Refresh-Wins
    foreach($line in [Fs.N]::Wins){
        $p=$line.Split('|')
        if($p[2] -eq $title -and ($procId -eq 0 -or [int]$p[4] -eq $procId)){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
function Find-Other([int]$procId) {
    Refresh-Wins
    foreach($line in [Fs.N]::Wins){
        $p=$line.Split('|')
        $t=$p[2]
        if($t.StartsWith("DeskLanes") -and $t -ne "DeskLanes" -and $t -ne "DeskLanesMessageHost" -and [int]$p[4] -eq $procId -and $p[3] -eq "True"){
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16))
        }
    }
    return [IntPtr]::Zero
}
function Find-Host([int]$procId){
    for($i=0;$i -lt 6;$i++){ $h=Find-Win "DeskLanesMessageHost" $procId; if($h -ne [IntPtr]::Zero){return $h}; Start-Sleep -Milliseconds 250 }
    return [IntPtr]::Zero
}
function Find-Menu([int]$procId){
    Refresh-Wins
    foreach($line in [Fs.N]::Wins){
        $p=$line.Split('|')
        if($p[1] -eq "#32768" -and [int]$p[4] -eq $procId -and $p[3] -eq "True"){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
function Send-Key([IntPtr]$m,[byte]$vk){
    [void][Fs.N]::PostMessage($m,[Fs.N]::WmKeyDown,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [void][Fs.N]::PostMessage($m,[Fs.N]::WmKeyUp,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}
function Invoke-Item([int]$procId,[int]$nav,[int]$attempts){
    for($a=1;$a -le $attempts;$a++){
        $hostw=Find-Host $procId
        if($hostw -eq [IntPtr]::Zero){ Write-Host ("[S] attempt" + $a + ": no host window"); continue }
        [void][Fs.N]::PostMessage($hostw,[Fs.N]::TrayCallbackMessage,[IntPtr]::new([Fs.N]::TrayIconId),[IntPtr]::new([Fs.N]::WmRButtonUp))
        Start-Sleep -Milliseconds 500
        $m=Find-Menu $procId
        if($m -eq [IntPtr]::Zero){ Write-Host ("[S] attempt" + $a + ": menu did not open"); continue }
        for($i=1;$i -le $nav;$i++){ Send-Key $m ([Fs.N]::VkDown) }
        Send-Key $m ([Fs.N]::VkReturn)
        Start-Sleep -Milliseconds 800
        if((Find-Menu $procId) -eq [IntPtr]::Zero){ return $true }
    }
    return $false
}
function Width-Of([IntPtr]$h){
    $r=New-Object Fs.N+RECT
    [void][Fs.N]::GetWindowRect($h,[ref]$r)
    return ($r.Right - $r.Left)
}
function Click-Desktop([int]$x,[int]$y){
    [Fs.N]::mouse_event([Fs.N]::MoveEv -bor [Fs.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Fs.N]::mouse_event([Fs.N]::LeftDownEv -bor [Fs.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Fs.N]::mouse_event([Fs.N]::LeftUpEv -bor [Fs.N]::AbsEv,$x,$y,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

try {
    # 用默认配置启动，否则上一轮可能留下非全屏模式
    $cfgDir = "C:\Users\TTjustTT\AppData\Local\DeskLanes"
    $cfgPath = Join-Path $cfgDir "settings.json"
    if (Test-Path -LiteralPath $cfgPath) { Copy-Item -LiteralPath $cfgPath -Destination (Join-Path $cfgDir "settings.json.diagbar.bak") -Force }
    [System.IO.File]::WriteAllText($cfgPath, "{}")

    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $vsl=[Fs.N]::GetSystemMetrics(76); $vst=[Fs.N]::GetSystemMetrics(77)
    $vsw=[Fs.N]::GetSystemMetrics(78); $vsh=[Fs.N]::GetSystemMetrics(79)

    $proc=Start-Process -FilePath $exe -PassThru
    $pidv=$proc.Id
    $main=[IntPtr]::Zero
    for($w=0;$w -lt 25;$w++){ Start-Sleep -Milliseconds 1000; $main=Find-Win "DeskLanes" $pidv; if($main -ne [IntPtr]::Zero){ break } }
    if($main -eq [IntPtr]::Zero){ Write-Host "[S] ERROR: no main window"; throw "no main" }

    $r=Invoke-Item $pidv 4 3
    Write-Host ("[S] switch to sidebar: " + $r)
    Start-Sleep -Milliseconds 1200
    $bar=Find-Other $pidv
    if($bar -eq [IntPtr]::Zero){ Write-Host "[S] ERROR: no sidebar window"; throw "no sidebar" }
    $w1=Width-Of $bar
    Write-Host ("[S] sidebar width before click: " + $w1)

    Click-Desktop ([int]($vsl + $vsw * 0.5)) ([int]($vst + $vsh - 140))
    $w2=Width-Of $bar
    Write-Host ("[S] sidebar width after desktop click: " + $w2)
    if($w2 -le 60){ Write-Host "[S] RESULT: auto-collapse OK" } else { Write-Host "[S] RESULT: auto-collapse FAILED" }
} finally {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "[S] cleanup done"
}
