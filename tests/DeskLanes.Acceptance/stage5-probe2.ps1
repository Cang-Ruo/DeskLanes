#Requires -Version 5.1
<#
  stage5-probe2.ps1 - DeskLanes Phase 5 mechanism probe v2 (reliable EnumWindows-based)

  Fixes vs v1: PowerShell binds $null to "" for string params (breaks FindWindow wildcard),
  so all window discovery now goes through EnumWindows/EnumChildWindows callbacks that append
  to static C# lists (no script-scope capture issues, no title wildcard issues).

  Desktop view host can be Progman OR a WorkerW (it migrates) - both are scanned.
#>
$ErrorActionPreference = 'Stop'

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))
if (-not (Test-Path -LiteralPath $evidenceDir)) { New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct DsRect { public int Left, Top, Right, Bottom; }

public static class DsWin4 {
    public static List<string> Top = new List<string>();
    public static List<string> Kids = new List<string>();
    public static List<string> Grand = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out DsRect lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

    public static string Describe(IntPtr h) {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, 256);
        var tb = new System.Text.StringBuilder(256);
        GetWindowText(h, tb, 256);
        uint p = 0; GetWindowThreadProcessId(h, out p);
        return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), p, IsWindowVisible(h));
    }

    public static string GetRectText(IntPtr h) {
        DsRect r;
        if (!GetWindowRect(h, out r)) { return ""; }
        return string.Format("{0},{1},{2},{3}", r.Left, r.Top, r.Right, r.Bottom);
    }
}
'@

$topCb = {
    param($h, $l)
    [DsWin4]::Top.Add([DsWin4]::Describe($h))
    return $true
}
$kidCb = {
    param($h, $l)
    [DsWin4]::Kids.Add([DsWin4]::Describe($h))
    return $true
}
$grandCb = {
    param($h, $l)
    [DsWin4]::Grand.Add([DsWin4]::Describe($h))
    return $true
}

function Get-IntPtrFromEntry([string]$entry) {
    return [IntPtr]::new([Convert]::ToInt64($entry.Split('|')[0].Substring(2), 16))
}

function Get-DesktopViews {
    [DsWin4]::Top.Clear()
    [void][DsWin4]::EnumWindows($topCb, [IntPtr]::Zero)
    $views = @()
    foreach ($line in [DsWin4]::Top) {
        $parts = $line.Split('|')
        if ($parts[1] -ne 'Progman' -and $parts[1] -ne 'WorkerW') { continue }
        $hostHwnd = Get-IntPtrFromEntry $line
        [DsWin4]::Kids.Clear()
        [void][DsWin4]::EnumChildWindows($hostHwnd, $kidCb, [IntPtr]::Zero)
        foreach ($k in [DsWin4]::Kids) {
            $kp = $k.Split('|')
            if ($kp[1] -ne 'SHELLDLL_DefView') { continue }
            $dvHwnd = Get-IntPtrFromEntry $k
            $lvEntry = $null
            [DsWin4]::Grand.Clear()
            [void][DsWin4]::EnumChildWindows($dvHwnd, $grandCb, [IntPtr]::Zero)
            foreach ($g in [DsWin4]::Grand) {
                if ($g.Split('|')[1] -eq 'SysListView32') { $lvEntry = $g; break }
            }
            $rectText = [DsWin4]::GetRectText($dvHwnd)
            if ($rectText -ne '') {
                $rp = $rectText.Split(',')
                $views += [pscustomobject]@{
                    Host = $parts[1]; HostEntry = $line; DefViewEntry = $k; ListViewEntry = $lvEntry
                    DefView = $dvHwnd
                    DefVisible = [DsWin4]::IsWindowVisible($dvHwnd)
                    LvVisible = $(if ($null -eq $lvEntry) { 'n/a' } else { ($lvEntry.Split('|')[4] -eq 'True') })
                    Left = [int]$rp[0]; Top = [int]$rp[1]; Right = [int]$rp[2]; Bottom = [int]$rp[3]
                }
            } else {
                $views += [pscustomobject]@{
                    Host = $parts[1]; HostEntry = $line; DefViewEntry = $k; ListViewEntry = $lvEntry
                    DefView = $dvHwnd
                    DefVisible = [DsWin4]::IsWindowVisible($dvHwnd)
                    LvVisible = $(if ($null -eq $lvEntry) { 'n/a' } else { ($lvEntry.Split('|')[4] -eq 'True') })
                    Left = $null; Top = $null; Right = $null; Bottom = $null
                }
            }
        }
    }
    return ,$views
}

function Get-MonitorOfView($v) {
    if ($null -eq $v.Left) { return 'rect-unavailable' }
    $cx = [int](($v.Left + $v.Right) / 2); $cy = [int](($v.Top + $v.Bottom) / 2)
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        $b = $s.Bounds
        if ($cx -ge $b.Left -and $cx -lt $b.Right -and $cy -ge $b.Top -and $cy -lt $b.Bottom) {
            if ($s.Primary) { return "$($s.DeviceName) [primary]" } else { return $s.DeviceName }
        }
    }
    return 'off-screen/unknown'
}

function Print-Views($label, $views) {
    Write-Host ("[{0}] desktop views = {1}; monitors = {2}" -f $label, $views.Count, [System.Windows.Forms.Screen]::AllScreens.Count)
    foreach ($v in $views) {
        $lvText = $(if ($null -eq $v.ListViewEntry) { 'none' } else { $v.ListViewEntry.Split('|')[0] })
        Write-Host ("    host={0} def={1} lv={2} defVisible={3} lvVisible={4} screen={5}" -f $v.Host, $v.DefViewEntry.Split('|')[0], $lvText, $v.DefVisible, $v.LvVisible, (Get-MonitorOfView $v))
    }
}

function Test-HiddenState($views) {
    # hidden = no view at all, or every view invisible
    if ($views.Count -eq 0) { return @{ Hidden = $true; Note = 'no-defview' } }
    $invisible = @($views | Where-Object { -not $_.DefVisible }).Count
    if ($invisible -eq $views.Count) { return @{ Hidden = $true; Note = 'all-invisible' } }
    if ($invisible -eq 0) { return @{ Hidden = $false; Note = 'all-visible' } }
    return @{ Hidden = $false; Note = "mixed ($invisible/$($views.Count) invisible)" }
}

function Wait-Hidden([int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $views = Get-DesktopViews
        $st = Test-HiddenState $views
        if ($st.Hidden) { $sw.Stop(); return @{ Ok = $true; Elapsed = $sw.ElapsedMilliseconds; Views = $views; Note = $st.Note } }
        Start-Sleep -Milliseconds 200
    }
    $sw.Stop()
    return @{ Ok = $false; Elapsed = $sw.ElapsedMilliseconds; Views = (Get-DesktopViews); Note = (Test-HiddenState (Get-DesktopViews)).Note }
}

function Wait-Shown([int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $views = Get-DesktopViews
        if ($views.Count -gt 0) {
            $visible = @($views | Where-Object { $_.DefVisible }).Count
            if ($visible -eq $views.Count) { $sw.Stop(); return @{ Ok = $true; Elapsed = $sw.ElapsedMilliseconds; Views = $views; Note = 'all-visible' } }
        }
        Start-Sleep -Milliseconds 200
    }
    $sw.Stop()
    $v2 = Get-DesktopViews
    return @{ Ok = $false; Elapsed = $sw.ElapsedMilliseconds; Views = $v2; Note = "still-not-shown (views=$($v2.Count))" }
}

function Save-AllScreens($prefix) {
    $i = 0
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        if ($s.Bounds.Width -le 0 -or $s.Bounds.Height -le 0) { continue }
        $bmp = New-Object System.Drawing.Bitmap($s.Bounds.Width, $s.Bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CopyFromScreen($s.Bounds.Location, [System.Drawing.Point]::Empty, $s.Bounds.Size)
            $tag = $(if ($s.Primary) { 'primary' } else { "disp$i" })
            $bmp.Save((Join-Path $evidenceDir ("{0}-{1}.png" -f $prefix, $tag)), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $g.Dispose(); $bmp.Dispose()
        }
        $i++
    }
}

function Get-DesktopFingerprint {
    $dirs = @()
    $d = [Environment]::GetFolderPath('Desktop'); if ($d) { $dirs += $d }
    $p = [Environment]::GetFolderPath('CommonDesktopDirectory'); if ($p) { $dirs += $p }
    $total = 0; $max = [DateTime]::MinValue
    foreach ($dir in $dirs) {
        $items = Get-ChildItem -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        $total += $items.Count
        foreach ($it in $items) { if ($it.LastWriteTimeUtc -gt $max) { $max = $it.LastWriteTimeUtc } }
    }
    return ("count={0} maxWriteUtc={1:o}" -f $total, $max)
}

function Broadcast-SettingChange([string]$section, [IntPtr]$target) {
    $r = [IntPtr]::Zero
    if ($target.ToInt64() -eq 0) { $target = [DsWin4]::HWND_BROADCAST }
    [void][DsWin4]::SendMessageTimeout($target, [DsWin4]::WM_SETTINGCHANGE, [IntPtr]::Zero, $section, [DsWin4]::SMTO_ABORTIFHUNG, 3000, [ref]$r)
}

function Set-HideIcons([int]$val) {
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $val
}

function Get-RegHideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return [int]$v.HideIcons
}

$orig = Get-RegHideIcons
$origPresent = $null -ne $orig
Write-Output "===== Phase 5 probe v2 start ====="
Write-Output ("[baseline] HideIcons = {0}" -f $(if ($origPresent) { "$orig" } else { "ABSENT (default = show)" }))
$fpBefore = Get-DesktopFingerprint
Write-Output ("[baseline] desktop fingerprint: {0}" -f $fpBefore)

try {
    $base = Get-DesktopViews
    Print-Views 'baseline' $base
    Save-AllScreens 'p2-01-baseline'

    Write-Output "--- A: HIDE via registry write only (no broadcast) ---"
    Set-HideIcons 1
    $r = Wait-Hidden 5000
    Print-Views 'after-hide' $r.Views
    Write-Output ("    => hidden by registry write alone: {0} in {1} ms ({2})" -f $r.Ok, $r.Elapsed, $r.Note)
    Save-AllScreens 'p2-02-hidden'

    Write-Output "--- B: SHOW via registry write only (no broadcast) ---"
    Set-HideIcons 0
    $r = Wait-Shown 8000
    Print-Views 'after-show-registry-only' $r.Views
    Write-Output ("    => shown by registry write alone: {0} in {1} ms ({2})" -f $r.Ok, $r.Elapsed, $r.Note)
    Save-AllScreens 'p2-03-shown'

    Write-Output "--- C: HIDE again + SHOW with WM_SETTINGCHANGE broadcast ---"
    Set-HideIcons 1
    $r = Wait-Hidden 5000
    Write-Output ("    => hidden (2nd): {0} in {1} ms ({2})" -f $r.Ok, $r.Elapsed, $r.Note)
    Set-HideIcons 0
    Broadcast-SettingChange 'ImmersiveShell' ([IntPtr]::Zero)
    $r = Wait-Shown 8000
    Write-Output ("    => shown with broadcast: {0} in {1} ms ({2})" -f $r.Ok, $r.Elapsed, $r.Note)
    Save-AllScreens 'p2-04-shown-broadcast'

    Write-Output "--- D: HIDE with broadcast too (does broadcast speed anything up?) ---"
    Set-HideIcons 1
    Broadcast-SettingChange 'ImmersiveShell' ([IntPtr]::Zero)
    $r = Wait-Hidden 5000
    Write-Output ("    => hidden with broadcast: {0} in {1} ms ({2})" -f $r.Ok, $r.Elapsed, $r.Note)

    Write-Output "--- E: mid fingerprint check ---"
    $fpMid = Get-DesktopFingerprint
    Write-Output ("    desktop fingerprint: {0} (unchanged: {1})" -f $fpMid, ($fpMid -eq $fpBefore))
}
finally {
    Write-Output "===== finally: restore registry to baseline ====="
    if ($origPresent) {
        Set-HideIcons $orig
    } else {
        Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    }
    Broadcast-SettingChange 'ImmersiveShell' ([IntPtr]::Zero)
    $r = Wait-Shown 8000
    $finalReg = Get-RegHideIcons
    Write-Output ("[final] HideIcons = {0} (baseline: {1}); shown: {2} ({3})" -f $(if ($null -eq $finalReg) { 'ABSENT' } else { $finalReg }), $(if ($origPresent) { $orig } else { 'ABSENT' }), $r.Ok, $r.Note)
    $fpAfter = Get-DesktopFingerprint
    Write-Output ("[final] desktop fingerprint: {0} (matches baseline: {1})" -f $fpAfter, ($fpAfter -eq $fpBefore))
    Save-AllScreens 'p2-05-final'
}
Write-Output "===== probe v2 end ====="
