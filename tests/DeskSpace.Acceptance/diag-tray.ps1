#Requires -Version 5.1
<#
  diag-tray.ps1 - Is the DeskSpace tray icon findable via UIA on this Win11 26H2 build?
  Dumps the whole UIA subtree of Shell_TrayWnd (names + control types) and takes screenshots.
#>
$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskSpace.App\bin\Debug\net10.0-windows\DeskSpace.exe"
$evidenceDir = Join-Path $repoRoot "docs\stage5-evidence"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DsTray {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
}
'@

# 1. 干净启动
Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Write-Output "=== starting DeskSpace ==="
$p1 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 5000
$c1 = (Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Output ("after 1st launch: process count = {0}" -f $c1)

# 2. 第二次启动（单实例测试）
Write-Output "=== launching 2nd instance ==="
$p2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 5000
$c2 = (Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Output ("after 2nd launch: process count = {0}" -f $c2)
Write-Output ("2nd instance HasExited = {0}" -f $p2.HasExited)

# 3. dump taskbar UIA tree
Write-Output "=== taskbar UIA subtree ==="
$tray = [DsTray]::FindWindow('Shell_TrayWnd', $null)
if ($tray -eq [IntPtr]::Zero) {
    Write-Output "Shell_TrayWnd NOT FOUND"
} else {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($tray)
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ("descendant count = {0}" -f $all.Count)
    foreach ($d in $all) {
        $name = $d.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        if (($name -is [string]) -and ($name.Length -gt 0)) {
            Write-Output ("  {0} : {1}" -f $d.Current.ControlType.ProgrammaticName, $name)
        }
    }
}

# 4. screenshot taskbar area (bottom-right of primary screen) + full screen
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $evidenceDir "diag-tray-full.png"), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally { $g.Dispose(); $bmp.Dispose() }

# crop bottom-right 420x60 (taskbar tray area)
$cropW = 460; $cropH = 70
$src = [System.Drawing.Bitmap]::FromFile((Join-Path $evidenceDir "diag-tray-full.png"))
$rect = New-Object System.Drawing.Rectangle(($b.Width - $cropW), ($b.Height - $cropH), $cropW, $cropH)
$crop = $src.Clone($rect, $src.PixelFormat)
$crop.Save((Join-Path $evidenceDir "diag-tray-crop.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose(); $src.Dispose()
Write-Output ("screenshots saved to {0}" -f $evidenceDir)
