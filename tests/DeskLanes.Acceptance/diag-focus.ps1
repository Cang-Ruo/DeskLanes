# Probe: why does the main window not get foreground focus after a tray-menu mode switch?
# Compare (a) menu-driven switch (synthetic PostMessage) vs (b) real keybd_event hotkey.
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"

Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;using System.Text;
namespace Fw {
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
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk,byte scan,uint flags,IntPtr extra);
        public const uint WmKeyDown=0x0100, WmKeyUp=0x0101;
        public const uint TrayCallbackMessage=0x8001;
        public const int TrayIconId=0x5201;
        public const int WmRButtonUp=0x0205;
        public const byte VkDown=0x28, VkReturn=0x0D;
        public const byte VkControl=0x11, VkMenu=0x12, VkSpace=0x20;
        public const uint KeyUp=0x0002;
        public static string Describe(IntPtr h){var sb=new StringBuilder(256);GetClassName(h,sb,256);var tb=new StringBuilder(256);GetWindowText(h,tb,256);uint pid=0;GetWindowThreadProcessId(h,out pid);return string.Format("0x{0:X}|{1}|{2}|{3}|{4}",h.ToInt64(),sb.ToString(),tb.ToString(),IsWindowVisible(h),pid);}
    }
}
"@

function Refresh-Wins {
    [Fw.N]::Wins.Clear()
    [void][Fw.N]::EnumWindows({param($h,$l) try { [Fw.N]::Wins.Add([Fw.N]::Describe($h)) } catch {} return $true },[IntPtr]::Zero)
}
function Find-Win([string]$title,[int]$procId) {
    Refresh-Wins
    foreach($line in [Fw.N]::Wins){
        $p=$line.Split('|')
        if($p[2] -eq $title -and ($procId -eq 0 -or [int]$p[4] -eq $procId)){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
function Find-Host([int]$procId){
    for($i=0;$i -lt 6;$i++){
        $h=Find-Win "DeskLanesMessageHost" $procId
        if($h -ne [IntPtr]::Zero){return $h}
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}
function Find-Menu([int]$procId){
    Refresh-Wins
    foreach($line in [Fw.N]::Wins){
        $p=$line.Split('|')
        if($p[1] -eq "#32768" -and [int]$p[4] -eq $procId -and $p[3] -eq "True"){ return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) }
    }
    return [IntPtr]::Zero
}
# find the floating window: title starts with "DeskLanes" but is not the main window nor the message host
function Find-Floating([int]$procId) {
    Refresh-Wins
    foreach($line in [Fw.N]::Wins){
        $p=$line.Split('|')
        $t=$p[2]
        if($t.StartsWith("DeskLanes") -and $t -ne "DeskLanes" -and $t -ne "DeskLanesMessageHost" -and ([int]$p[4] -eq $procId)){
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16))
        }
    }
    return [IntPtr]::Zero
}
function Send-Key([IntPtr]$m,[byte]$vk){
    [void][Fw.N]::PostMessage($m,[Fw.N]::WmKeyDown,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [void][Fw.N]::PostMessage($m,[Fw.N]::WmKeyUp,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}
function Invoke-Item([int]$procId,[int]$nav,[int]$attempts){
    for($a=1;$a -le $attempts;$a++){
        $hostw=Find-Host $procId
        if($hostw -eq [IntPtr]::Zero){ Write-Host ("[F] attempt" + $a + ": no host window"); continue }
        [void][Fw.N]::PostMessage($hostw,[Fw.N]::TrayCallbackMessage,[IntPtr]::new([Fw.N]::TrayIconId),[IntPtr]::new([Fw.N]::WmRButtonUp))
        Start-Sleep -Milliseconds 500
        $m=Find-Menu $procId
        if($m -eq [IntPtr]::Zero){ Write-Host ("[F] attempt" + $a + ": menu did not open"); continue }
        for($i=1;$i -le $nav;$i++){ Send-Key $m ([Fw.N]::VkDown) }
        Send-Key $m ([Fw.N]::VkReturn)
        Start-Sleep -Milliseconds 800
        if((Find-Menu $procId) -eq [IntPtr]::Zero){ return $true }
    }
    return $false
}
function Fg-Info([IntPtr]$mainHwnd){
    $fg=[Fw.N]::GetForegroundWindow()
    Refresh-Wins
    $desc="(not enumerated)"
    $fgs="0x" + ("{0:X}" -f $fg.ToInt64())
    foreach($line in [Fw.N]::Wins){ if($line.StartsWith($fgs + "|")){ $desc=$line; break } }
    $eq = ($fg -eq $mainHwnd)
    Write-Host ("[F] foreground=" + $fgs + "  main=0x" + ("{0:X}" -f $mainHwnd.ToInt64()) + "  equal=" + $eq)
    Write-Host ("[F] fg desc: " + $desc)
}

try {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc=Start-Process -FilePath $exe -PassThru
    $pidv=$proc.Id
    $main=[IntPtr]::Zero
    for($w=0;$w -lt 25;$w++){
        Start-Sleep -Milliseconds 1000
        $main=Find-Win "DeskLanes" $pidv
        if($main -ne [IntPtr]::Zero){ Write-Host ("[F] main up at ~" + ($w+1) + "s"); break }
    }
    if($main -eq [IntPtr]::Zero){ Write-Host "[F] ERROR: no main window"; throw "no main" }

    Write-Host "--- (a) menu-driven switch: 4=sidebar then 3=fullscreen ---"
    $r1 = Invoke-Item $pidv 4 3
    Write-Host ("[F] switch to sidebar invoke result: " + $r1)
    Start-Sleep -Milliseconds 800
    $r2 = Invoke-Item $pidv 3 3
    Write-Host ("[F] switch to fullscreen invoke result: " + $r2)
    Start-Sleep -Milliseconds 500
    $main=Find-Win "DeskLanes" $pidv
    Write-Host "[F] after menu-switch, polling focus up to 3s..."
    for($t=0;$t -lt 6;$t++){
        Fg-Info $main
        if([Fw.N]::GetForegroundWindow() -eq $main){ Write-Host "[F] GOT FOCUS (menu path)"; break }
        Start-Sleep -Milliseconds 500
    }

    Write-Host "--- (b) hotkey (REAL keybd_event) summons floating window ---"
    [Fw.N]::keybd_event([Fw.N]::VkControl,0,0,[IntPtr]::Zero)
    [Fw.N]::keybd_event([Fw.N]::VkMenu,0,0,[IntPtr]::Zero)
    [Fw.N]::keybd_event([Fw.N]::VkSpace,0,0,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Fw.N]::keybd_event([Fw.N]::VkSpace,0,[Fw.N]::KeyUp,[IntPtr]::Zero)
    [Fw.N]::keybd_event([Fw.N]::VkMenu,0,[Fw.N]::KeyUp,[IntPtr]::Zero)
    [Fw.N]::keybd_event([Fw.N]::VkControl,0,[Fw.N]::KeyUp,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    $flt=Find-Floating $pidv
    Write-Host ("[F] floating window=0x" + ("{0:X}" -f $flt.ToInt64()))
    if($flt -ne [IntPtr]::Zero){
        $fgi=[Fw.N]::GetForegroundWindow()
        Write-Host ("[F] hotkey: foreground==floating ? " + ($fgi -eq $flt))
    }
} finally {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "[F] cleanup done"
}
