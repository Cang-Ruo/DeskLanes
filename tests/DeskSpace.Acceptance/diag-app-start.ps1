# 最小探针：确认 DeskSpace 能否启动、主窗口何时出现、宿主窗口与托盘菜单能否定位。
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskSpace.App\bin\Debug\net10.0-windows\DeskSpace.exe"

Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Collections.Generic;
namespace Dg2 {
    public static class N {
        public static List<string> Wins=new List<string>();
        public delegate bool EWP(IntPtr h,IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EWP cb,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,System.Text.StringBuilder s,int n);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        public static string Describe(IntPtr h){var sb=new System.Text.StringBuilder(256);GetClassName(h,sb,256);var tb=new System.Text.StringBuilder(256);GetWindowText(h,tb,256);uint pid=0;GetWindowThreadProcessId(h,out pid);return string.Format("0x{0:X}|{1}|{2}|{3}|{4}",h.ToInt64(),sb.ToString(),tb.ToString(),IsWindowVisible(h),pid);}
    }
}
"@

function Enum-DS([int]$pidv) {
    [Dg2.N]::Wins.Clear(); [void][Dg2.N]::EnumWindows({param($h,$l)[Dg2.N]::Wins.Add([Dg2.N]::Describe($h));$true},[IntPtr]::Zero)
    $out=@()
    foreach ($line in [Dg2.N]::Wins) { $p=$line.Split('|'); if (($p[4]-eq [string]$pidv) -or ($p[2] -like "*DeskSpace*") -or ($p[1] -like "*DeskSpace*")) { $out += $line } }
    return $out
}

Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$proc = Start-Process -FilePath $exe -PassThru
Write-Host ("[PROBE] 已启动 PID={0}" -f $proc.Id)
for ($w=1;$w-le 12;$w++) {
    Start-Sleep -Milliseconds 1000
    $proc.Refresh()
    $found = Enum-DS $proc.Id
    $main = $found | Where-Object { $_ -match "\|DeskSpace\|" }
    Write-Host ("[PROBE] t={0}s 进程退出={1} 匹配窗口数={2} 主窗口={3}" -f $w, $proc.HasExited, $found.Count, ($main.Count -gt 0))
    if ($main.Count -gt 0) { foreach ($f in $found) { Write-Host ("   WIN {0}" -f $f) } ; break }
}
if ($proc.HasExited) { Write-Host ("[PROBE] 应用已退出，退出码={0}" -f $proc.ExitCode) }
Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "[PROBE] 清理完成"
