Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Diag {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static class W {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    }
}
"@
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath "$env:USERPROFILE\Documents\Github\DeskLanes\src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe" -PassThru
Start-Sleep -Seconds 6
$targetPid = $p.Id
$lines = New-Object System.Collections.ArrayList
$cb = [Diag.EnumProc]{
    param($h, $l)
    $winPid = 0
    [Diag.W]::GetWindowThreadProcessId($h, [ref]$winPid) | Out-Null
    if ($winPid -eq $targetPid) {
        $sb = New-Object System.Text.StringBuilder 256
        [Diag.W]::GetWindowText($h, $sb, 256) | Out-Null
        [void]$lines.Add(("hwnd={0} visible={1} title=[{2}]" -f $h, [Diag.W]::IsWindowVisible($h), $sb.ToString()))
    }
    return $true
}
[Diag.W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$lines | ForEach-Object { Write-Host $_ }
Write-Host ("ForegroundWindow = {0}" -f [Diag.W]::GetForegroundWindow())
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
