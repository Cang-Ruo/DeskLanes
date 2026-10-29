#Requires -Version 5.1
<#
  diag-tray2.ps1 - verify single-instance fix + discover Win11 tray overflow flyout structure
#>
$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"
$evidenceDir = Join-Path $repoRoot "docs\stage5-evidence"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class DsTray2 {
    public static List<string> Top = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
}
'@

$topCb = { param($h, $l) [DsTray2]::Top.Add([DsTray2]::ClassOf($h)); return $true }

# 1. single-instance verification
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
[void](Start-Process -FilePath $exe -PassThru)
Start-Sleep -Milliseconds 4000
$p2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 4000
$c = (Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Output ("[single-instance] process count after 2nd launch (expect 1): {0}" -f $c)
Write-Output ("[single-instance] 2nd instance HasExited (expect True): {0}" -f $p2.HasExited)

# 2. find chevron and open overflow flyout
$tray = [System.Windows.Automation.AutomationElement]::FromHandle(
    ([System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))).Current.NativeWindowHandle)
$chevron = $null
$all = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
foreach ($d in $all) {
    $name = $d.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    if (($name -is [string]) -and $name.Contains('显示隐藏的图标')) { $chevron = $d; break }
}
if ($null -eq $chevron) {
    Write-Output "[overflow] chevron button NOT found"
} else {
    Write-Output ("[overflow] chevron found: '{0}'" -f $chevron.Current.Name)
    try {
        $ip = $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
        Write-Output "[overflow] chevron invoked"
    } catch {
        Write-Output ("[overflow] Invoke failed: {0}" -f $_.Exception.Message)
    }
    Start-Sleep -Milliseconds 1200

    # dump all top-level windows to find the flyout
    [DsTray2]::Top.Clear()
    [void][DsTray2]::EnumWindows($topCb, [IntPtr]::Zero)
    Write-Output "[overflow] visible top-level windows after chevron click:"
    foreach ($cls in [DsTray2]::Top) {
        Write-Output ("    {0}" -f $cls)
    }

    # dump UIA roots that contain the DeskLanes tray icon
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $deskIcons = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'DeskLanes')))
    Write-Output ("[overflow] UIA elements named 'DeskLanes' at root level: {0}" -f $deskIcons.Count)
    foreach ($d in $deskIcons) {
        Write-Output ("    class={0} type={1}" -f $d.Current.ClassName, $d.Current.ControlType.ProgrammaticName)
    }
}

# close flyout
[DsTray2]::keybd_event(0x1B, 0, 0, 0)
Start-Sleep -Milliseconds 60
[DsTray2]::keybd_event(0x1B, 0, 2, 0)

# screenshot with flyout open (before closing) - redo quickly
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "=== done (DeskLanes killed) ==="
