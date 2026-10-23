#Requires -Version 5.1
<#
  stage5-probe.ps1 - DeskSpace Phase 5 mechanism probe (native desktop icon hide/restore)

  Verifies on real Win11 26H2 / build 26300:
    1. Enumerate desktop window hierarchy: Progman / WorkerW / SHELLDLL_DefView / SysListView32,
       visibility per monitor
    2. Read/write semantics of HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\HideIcons
    3. Whether a plain registry write applies WITHOUT restarting Explorer, and how long each
       direction (hide / show) takes; whether WM_SETTINGCHANGE broadcast changes the outcome
    4. "Actual display state" detection: DefView window visibility + full screen screenshots
    5. Desktop file fingerprint before/after: proves no desktop file was moved/deleted/modified
    6. Restore semantics: original value present -> write back original; absent -> delete value

  Safety:
    - Writes exactly ONE registry value (Explorer\Advanced\HideIcons) - the same value that the
      Windows desktop context menu "View -> Show desktop icons" checkbox itself writes
    - Never kills/restarts Explorer.exe
    - Never touches any file on the desktop
    - try/finally guarantees the original registry value is restored and verified
#>
$ErrorActionPreference = 'Stop'

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))
if (-not (Test-Path -LiteralPath $evidenceDir)) { New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct DsRect { public int Left, Top, Right, Bottom; }

public static class DsWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out DsRect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
}
'@

function Get-RegHideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return [int]$v.HideIcons
}

function Get-DesktopViews {
    $rows = @()
    $progman = [DsWin]::FindWindow('Progman', $null)
    if ($progman.ToInt64() -ne 0) {
        $dv = [DsWin]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dv.ToInt64() -ne 0) {
            $lv = [DsWin]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', $null)
            $rows += [pscustomobject]@{
                Host = 'Progman'; DefView = $dv; ListView = $lv
                DefVisible = [DsWin]::IsWindowVisible($dv)
                LvVisible = $(if ($lv.ToInt64() -eq 0) { 'n/a' } else { [DsWin]::IsWindowVisible($lv) })
            }
        }
    }
    $w = [DsWin]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
    while ($w.ToInt64() -ne 0) {
        $dvW = [DsWin]::FindWindowEx($w, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dvW.ToInt64() -ne 0) {
            $lvW = [DsWin]::FindWindowEx($dvW, [IntPtr]::Zero, 'SysListView32', $null)
            $rows += [pscustomobject]@{
                Host = ("WorkerW:0x{0:X}" -f $w.ToInt64()); DefView = $dvW; ListView = $lvW
                DefVisible = [DsWin]::IsWindowVisible($dvW)
                LvVisible = $(if ($lvW.ToInt64() -eq 0) { 'n/a' } else { [DsWin]::IsWindowVisible($lvW) })
            }
        }
        $w = [DsWin]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', $null)
    }
    foreach ($r in $rows) {
        $rect = New-Object -TypeName 'DsWin+DsRect'
        $ok = [DsWin]::GetWindowRect($r.DefView, [ref]$rect)
        if ($ok) {
            $r | Add-Member -NotePropertyName Left -NotePropertyValue $rect.Left
            $r | Add-Member -NotePropertyName Top -NotePropertyValue $rect.Top
            $r | Add-Member -NotePropertyName Right -NotePropertyValue $rect.Right
            $r | Add-Member -NotePropertyName Bottom -NotePropertyValue $rect.Bottom
        }
    }
    return $rows
}

function Get-MonitorOfRect($r) {
    if ($null -eq $r.Left) { return 'rect-unavailable' }
    $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
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
        $lvText = $(if ($v.ListView.ToInt64() -eq 0) { '0' } else { ('0x{0:X}' -f $v.ListView.ToInt64()) })
        Write-Host ("    {0,-18} def=0x{1:X} lv={2} defVisible={3} lvVisible={4} screen={5}" -f $v.Host, $v.DefView.ToInt64(), $lvText, $v.DefVisible, $v.LvVisible, (Get-MonitorOfRect $v))
    }
}

function Wait-ViewsState([bool]$wantVisible, [int]$timeoutMs) {
    # Poll until ALL desktop views reach the wanted visibility (or timeout)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $views = Get-DesktopViews
        if ($views.Count -eq 0) { return @{ Ok = $false; Elapsed = $sw.ElapsedMilliseconds; Views = $views; Note = 'no views' } }
        $matchCount = @($views | Where-Object { $_.DefVisible -eq $wantVisible }).Count
        if ($matchCount -eq $views.Count) {
            $sw.Stop()
            return @{ Ok = $true; Elapsed = $sw.ElapsedMilliseconds; Views = $views }
        }
        Start-Sleep -Milliseconds 200
    }
    $sw.Stop()
    return @{ Ok = $false; Elapsed = $sw.ElapsedMilliseconds; Views = (Get-DesktopViews) }
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
    if ($target.ToInt64() -eq 0) { $target = [DsWin]::HWND_BROADCAST }
    [void][DsWin]::SendMessageTimeout($target, [DsWin]::WM_SETTINGCHANGE, [IntPtr]::Zero, $section, [DsWin]::SMTO_ABORTIFHUNG, 3000, [ref]$r)
}

function Set-HideIcons([int]$val) {
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $val
}

$orig = Get-RegHideIcons
$origPresent = $null -ne $orig
Write-Output "===== Phase 5 probe start ====="
Write-Output ("[baseline] HideIcons = {0}" -f $(if ($origPresent) { "$orig" } else { "ABSENT (default = show)" }))
$sysIcons = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel'
Write-Output ("[info] system-icon settings key exists: {0}" -f (Test-Path -LiteralPath $sysIcons))
$pol = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
$polVals = $(if (Test-Path -LiteralPath $pol) { ((Get-ItemProperty -Path $pol).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { $_.Name }) -join ',' } else { 'no-policy-key' })
Write-Output ("[info] Explorer policies: [{0}]" -f $polVals)

$fpBefore = Get-DesktopFingerprint
Write-Output ("[baseline] desktop fingerprint: {0}" -f $fpBefore)

try {
    $base = Get-DesktopViews
    Print-Views 'baseline' $base
    Save-AllScreens '01-baseline'

    Write-Output "--- A: HIDE via registry write only (no broadcast), poll up to 3s ---"
    Set-HideIcons 1
    $r = Wait-ViewsState $false 3000
    Print-Views 'after-hide' $r.Views
    Write-Output ("    => hide by registry write alone: {0} in {1} ms" -f $r.Ok, $r.Elapsed)
    Save-AllScreens '02-hidden'
    $fpMid = Get-DesktopFingerprint
    Write-Output ("[mid] desktop fingerprint: {0} (unchanged: {1})" -f $fpMid, ($fpMid -eq $fpBefore))

    Write-Output "--- B: SHOW via registry write only (no broadcast), poll up to 5s ---"
    Set-HideIcons 0
    $r = Wait-ViewsState $true 5000
    Print-Views 'after-show-registry-only' $r.Views
    Write-Output ("    => show by registry write alone: {0} in {1} ms" -f $r.Ok, $r.Elapsed)
    Save-AllScreens '03-shown'

    Write-Output "--- C: HIDE again, then SHOW with WM_SETTINGCHANGE broadcast, poll up to 5s ---"
    Set-HideIcons 1
    $r = Wait-ViewsState $false 3000
    Write-Output ("    => hide (2nd): {0} in {1} ms" -f $r.Ok, $r.Elapsed)
    Set-HideIcons 0
    Broadcast-SettingChange 'ImmersiveShell' ([IntPtr]::Zero)
    $r = Wait-ViewsState $true 5000
    Print-Views 'after-show-with-broadcast' $r.Views
    Write-Output ("    => show with broadcast: {0} in {1} ms" -f $r.Ok, $r.Elapsed)
    Save-AllScreens '04-shown-broadcast'

    if ($origPresent) {
        Write-Output ("--- D: original value present ({0}), exact restore happens in finally ---" -f $orig)
    } else {
        Write-Output "--- D: original value absent; test DELETE semantics (expect still shown) ---"
        Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
        $r = Wait-ViewsState $true 5000
        Print-Views 'after-value-deleted' $r.Views
        Write-Output ("    => absent behaves as shown: {0} in {1} ms" -f $r.Ok, $r.Elapsed)
    }
}
finally {
    Write-Output "===== finally: restore registry to baseline ====="
    if ($origPresent) {
        Set-HideIcons $orig
    } else {
        Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    }
    Broadcast-SettingChange 'ImmersiveShell' ([IntPtr]::Zero)
    $r = Wait-ViewsState $true 5000
    $finalReg = Get-RegHideIcons
    Write-Output ("[final] HideIcons = {0} (baseline: {1}); all views visible: {2}" -f $(if ($null -eq $finalReg) { 'ABSENT' } else { $finalReg }), $(if ($origPresent) { $orig } else { 'ABSENT' }), $r.Ok)
    $fpAfter = Get-DesktopFingerprint
    Write-Output ("[final] desktop fingerprint: {0} (matches baseline: {1})" -f $fpAfter, ($fpAfter -eq $fpBefore))
    Save-AllScreens '05-final'
}
Write-Output "===== probe end ====="
