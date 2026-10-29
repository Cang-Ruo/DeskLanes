# Probe: capture the blocking #32770 dialog shown when DeskLanes starts with default config.
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;using System.Text;
namespace Dg3 {
    public static class N {
        public static List<string> Wins=new List<string>();
        public delegate bool EWP(IntPtr h,IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        public static string Describe(IntPtr h){var sb=new StringBuilder(256);GetClassName(h,sb,256);var tb=new StringBuilder(256);GetWindowText(h,tb,256);uint pid=0;GetWindowThreadProcessId(h,out pid);return string.Format("0x{0:X}|{1}|{2}|{3}|{4}",h.ToInt64(),sb.ToString(),tb.ToString(),IsWindowVisible(h),pid);}
    }
}
"@

function Enum-All {
    [Dg3.N]::Wins.Clear()
    [void][Dg3.N]::EnumWindows({param($h,$l) try { [Dg3.N]::Wins.Add([Dg3.N]::Describe($h)) } catch {} return $true }, [IntPtr]::Zero)
    return [Dg3.N]::Wins
}

$configDir=Join-Path $env:LOCALAPPDATA "DeskLanes"
$settingsFile=Join-Path $configDir "settings.json"
$backup=$null
if (Test-Path -LiteralPath $settingsFile) { $backup=Join-Path $env:TEMP ("Dg3Bak_"+[Guid]::NewGuid().ToString("N").Substring(0,6)); Copy-Item -LiteralPath $settingsFile -Destination $backup -Force; Set-Content -LiteralPath $settingsFile -Value "{}" -Encoding UTF8 }

try {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc = Start-Process -FilePath $exe -PassThru
    Write-Host ("[D3] launched PID={0}" -f $proc.Id)

    $dialog=$null
    for($w=1;$w -le 15;$w++){
        Start-Sleep -Milliseconds 1000
        $all = Enum-All
        $dlg = $all | Where-Object { $_ -match "\|#32770\|" }
        if ($dlg.Count -gt 0) { $dialog=$dlg[0]; Write-Host ("[D3] t={0}s DIALOG FOUND: {1}" -f $w, ($dlg -join " || ")); break }
        $ds = $all | Where-Object { $_ -match "DeskLanes" }
        if ($ds.Count -gt 0) { Write-Host ("[D3] t={0}s windows: {1}" -f $w, ($ds -join " || ")) }
    }

    if ($dialog) {
        $parts=$dialog.Split('|')
        $hwnd=[IntPtr]::new([Convert]::ToInt64($parts[0].Substring(2),16))
        $el=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        Write-Host ("[D3] dialog handle={0} name=[{1}]" -f $hwnd, $el.Current.Name)
        # dump descendants
        $cond=[System.Windows.Automation.Condition]::TrueCondition
        $kids=$el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        Write-Host ("[D3] descendants={0}" -f $kids.Count)
        foreach($k in $kids){
            $nm=$k.Current.Name
            $cls=$k.Current.ClassName
            $ct=$k.Current.ControlType.ProgrammaticName
            if ($nm -or $cls) { Write-Host ("   [{0}] class={1} name=[{2}]" -f $ct,$cls,$nm) }
        }
        # also try native child window texts
        Write-Host "[D3] done"
    } else {
        Write-Host "[D3] NO DIALOG found"
        $all = Enum-All
        Write-Host ("[D3] DeskLanes-related: {0}" -f (($all | Where-Object { $_ -match "DeskLanes" }) -join " || "))
    }
} finally {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    if ($backup -and (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $backup -Destination $settingsFile -Force }
    Write-Host "[D3] cleanup done"
}
