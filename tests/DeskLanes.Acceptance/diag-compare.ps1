Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Diag {
    public static class W {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
    }
}
"@
$env:DOTNET_ROOT = "C:\Users\TTjustTT\.dotnet"
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath "C:\Users\TTjustTT\Documents\Github\DeskLanes\src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe" -PassThru
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
$sb = New-Object System.Text.StringBuilder 256
[Diag.W]::GetWindowText($h, $sb, 256) | Out-Null
Write-Host ("MainWindowHandle={0} GetWindowText=[{1}] visible={2}" -f $h, $sb.ToString(), [Diag.W]::IsWindowVisible($h))
Write-Host ("FindWindow(null, 'DeskLanes')          = {0}" -f [Diag.W]::FindWindow($null, "DeskLanes"))
Write-Host ("FindWindow(null, 'DeskLanesMessageHost') = {0}" -f [Diag.W]::FindWindow($null, "DeskLanesMessageHost"))
Write-Host ("FindWindowEx(0,0,null,'DeskLanes')      = {0}" -f [Diag.W]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, $null, "DeskLanes"))
Write-Host ("FindWindow(null, 'Shell_TrayWnd')       = {0}" -f [Diag.W]::FindWindow($null, "Shell_TrayWnd"))
Write-Host ("FindWindow(null, null)                  = {0}" -f [Diag.W]::FindWindow($null, $null))
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
