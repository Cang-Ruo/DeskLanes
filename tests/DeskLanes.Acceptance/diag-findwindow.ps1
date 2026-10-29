Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Diag {
    public static class W {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
        public static extern IntPtr FindWindowW(string cls, string title);
    }
}
"@
$env:DOTNET_ROOT = "C:\Users\TTjustTT\.dotnet"
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath "C:\Users\TTjustTT\Documents\Github\DeskLanes\src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe" -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
Write-Host ("Process MainWindowHandle={0} Title=[{1}]" -f $p.MainWindowHandle, $p.MainWindowTitle)
$h = [Diag.W]::FindWindow($null, "DeskLanes")
Write-Host ("FindWindow(DeskLanes) = {0} visible={1}" -f $h, [Diag.W]::IsWindowVisible($h))
if ($h -ne [IntPtr]::Zero) {
    $sb = New-Object System.Text.StringBuilder 256
    [Diag.W]::GetWindowText($h, $sb, 256) | Out-Null
    Write-Host ("GetWindowText = [{0}]" -f $sb.ToString())
}
$h2 = [Diag.W]::FindWindowW($null, "DeskLanes")
Write-Host ("FindWindowW(DeskLanes) = {0} visible={1}" -f $h2, [Diag.W]::IsWindowVisible($h2))
$tray = [Diag.W]::FindWindow("Shell_TrayWnd", $null)
Write-Host ("FindWindow(Shell_TrayWnd) = {0}" -f $tray)
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
