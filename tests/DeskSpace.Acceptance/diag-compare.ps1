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
Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath "C:\Users\TTjustTT\Documents\Github\DeskSpace\src\DeskSpace.App\bin\Debug\net10.0-windows\DeskSpace.exe" -PassThru
Start-Sleep -Seconds 6
$h = $p.MainWindowHandle
$sb = New-Object System.Text.StringBuilder 256
[Diag.W]::GetWindowText($h, $sb, 256) | Out-Null
Write-Host ("MainWindowHandle={0} GetWindowText=[{1}] visible={2}" -f $h, $sb.ToString(), [Diag.W]::IsWindowVisible($h))
Write-Host ("FindWindow(null, 'DeskSpace')          = {0}" -f [Diag.W]::FindWindow($null, "DeskSpace"))
Write-Host ("FindWindow(null, 'DeskSpaceMessageHost') = {0}" -f [Diag.W]::FindWindow($null, "DeskSpaceMessageHost"))
Write-Host ("FindWindowEx(0,0,null,'DeskSpace')      = {0}" -f [Diag.W]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, $null, "DeskSpace"))
Write-Host ("FindWindow(null, 'Shell_TrayWnd')       = {0}" -f [Diag.W]::FindWindow($null, "Shell_TrayWnd"))
Write-Host ("FindWindow(null, null)                  = {0}" -f [Diag.W]::FindWindow($null, $null))
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
