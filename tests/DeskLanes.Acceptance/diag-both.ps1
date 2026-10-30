Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Diag {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static class W {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    }
}
"@
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath "$env:USERPROFILE\Documents\Github\DeskLanes\src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe" -PassThru
Start-Sleep -Seconds 6

# 1) EnumWindows 枚举所有可见顶层窗口标题
$titles = New-Object System.Collections.ArrayList
$cb = [Diag.EnumProc]{
    param($h, $l)
    if ([Diag.W]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [Diag.W]::GetWindowText($h, $sb, 256) | Out-Null
        if ($sb.Length -gt 0) { [void]$titles.Add($sb.ToString()) }
    }
    return $true
}
[Diag.W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
Write-Host ("EnumWindows 找到 {0} 个可见标题窗口，含 DeskLanes: {1}" -f $titles.Count, ($titles -contains "DeskLanes"))
Write-Host ("GetDesktopWindow = {0}  GetShellWindow = {1}" -f [Diag.W]::GetDesktopWindow(), [Diag.W]::GetShellWindow())

# 2) 同一进程里 FindWindow
Write-Host ("FindWindow(null,'DeskLanes') = {0}" -f [Diag.W]::FindWindow($null, "DeskLanes"))
Write-Host ("FindWindow(null,'Shell_TrayWnd') = {0}" -f [Diag.W]::FindWindow($null, "Shell_TrayWnd"))
Write-Host ("FindWindow('Shell_TrayWnd', null) = {0}" -f [Diag.W]::FindWindow("Shell_TrayWnd", $null))

# 3) 进程自身信息
Write-Host ("PS 进程: Id={0} Session={1}" -f $Global:PID, (Get-Process -Id $Global:PID).SessionId)
$appProc = Get-Process -Id $p.Id
Write-Host ("App 进程: Id={0} Session={1} Path={2}" -f $appProc.Id, $appProc.SessionId, $appProc.Path)
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
