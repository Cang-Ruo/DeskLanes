# Diagnostic 2: isolate whether sending Enter to the open tray menu actually
# INVOKES the highlighted item (menu closes) or merely sits there.
# Proven S4Win.N P/Invoke reused from stage4-accept.ps1.
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$exe = Join-Path $repoRoot "src\DeskLanes.App\bin\Debug\net10.0-windows\DeskLanes.exe"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
namespace S4Win {
    public static class N {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        public static string GetRectText(IntPtr h) {
            RECT r; if (!GetWindowRect(h, out r)) { return "0,0,0,0"; }
            return string.Format("{0},{1},{2},{3}", r.Left, r.Top, r.Right, r.Bottom);
        }
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr h, int id);
        public static List<string> Wins = new List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        public static string DescribeWin(IntPtr h) {
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
            uint pid = 0; GetWindowThreadProcessId(h, out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
        }
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);
        public const uint TrayCallbackMessage = 0x8001;
        public const int TrayIconId = 0x5201;
        public const int WmRButtonUp = 0x0205;
        public const uint WmKeyDown = 0x0100;
        public const uint WmKeyUp = 0x0101;
        public const byte VkDown = 0x28;
        public const byte VkReturn = 0x0D;
        public const byte VkControl = 0x11;
        public const byte VkMenu = 0x12;
        public const byte VkSpace = 0x20;
        public const uint KeyUp = 0x0002;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Find-WindowByTitle([string]$title) {
    [S4Win.N]::Wins.Clear()
    [void][S4Win.N]::EnumWindows({param($h,$l) try { [S4Win.N]::Wins.Add([S4Win.N]::DescribeWin($h)) } catch {} return $true }, [IntPtr]::Zero)
    foreach ($line in [S4Win.N]::Wins) { $p=$line.Split('|'); if ($p[2] -eq $title) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
    return [IntPtr]::Zero
}
function Find-MenuWindow([int]$procId) {
    [S4Win.N]::Wins.Clear()
    [void][S4Win.N]::EnumWindows({param($h,$l) try { [S4Win.N]::Wins.Add([S4Win.N]::DescribeWin($h)) } catch {} return $true }, [IntPtr]::Zero)
    foreach ($line in [S4Win.N]::Wins) { $p=$line.Split('|'); if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $procId) -and ($p[3] -eq "True")) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
    return [IntPtr]::Zero
}
function Find-Host([int]$procId) {
    for ($i=0;$i -lt 6;$i++) {
        [S4Win.N]::Wins.Clear()
        [void][S4Win.N]::EnumWindows({param($h,$l) try { [S4Win.N]::Wins.Add([S4Win.N]::DescribeWin($h)) } catch {} return $true }, [IntPtr]::Zero)
        foreach ($line in [S4Win.N]::Wins) { $p=$line.Split('|'); if (($p[2] -eq "DeskLanesMessageHost") -and ([int]$p[4] -eq $procId)) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2),16)) } }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}
function Send-MenuKey([IntPtr]$m,[byte]$vk) {
    [void][S4Win.N]::PostMessage($m,[S4Win.N]::WmKeyDown,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [void][S4Win.N]::PostMessage($m,[S4Win.N]::WmKeyUp,[IntPtr]::new($vk),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
}
function Nav([IntPtr]$m,[int]$n){ for($i=1;$i -le $n;$i++){ Send-MenuKey $m ([S4Win.N]::VkDown) } }

# ---- config: overwrite to default (no delete) ----
$configDir=Join-Path $env:LOCALAPPDATA "DeskLanes"
$settingsFile=Join-Path $configDir "settings.json"
$backup=$null
if (Test-Path -LiteralPath $settingsFile) { $backup=Join-Path $env:TEMP ("Dg2Bak_"+[Guid]::NewGuid().ToString("N").Substring(0,6)); Copy-Item -LiteralPath $settingsFile -Destination $backup -Force; Set-Content -LiteralPath $settingsFile -Value "{}" -Encoding UTF8 }

try {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc = Start-Process -FilePath $exe -PassThru
    $pidv = $proc.Id
    # wait up to 25s for main window (desktop scan of ~11k items is slow)
    $main=[IntPtr]::Zero
    for($w=0;$w -lt 25;$w++){
        Start-Sleep -Milliseconds 1000
        [S4Win.N]::Wins.Clear()
        [void][S4Win.N]::EnumWindows({param($h,$l) try { [S4Win.N]::Wins.Add([S4Win.N]::DescribeWin($h)) } catch {} return $true }, [IntPtr]::Zero)
        $dsWins = [S4Win.N]::Wins | Where-Object { $_ -match "DeskLanes" }
        if ($dsWins.Count -gt 0) { Write-Host ("[D2] t={0}s DeskLanes windows: {1}" -f ($w+1), ($dsWins -join " || ")) }
        $main=Find-WindowByTitle "DeskLanes"
        if($main -ne [IntPtr]::Zero){ Write-Host ("[D2] main window up at ~{0}s" -f ($w+1)); break }
    }
    if ($main -eq [IntPtr]::Zero) { Write-Host "[D2] ERROR: main window never appeared"; throw "no main window" }

    # open menu
    $hostw=Find-Host $pidv
    Write-Host ("[D2] host window={0}" -f $hostw)
    [void][S4Win.N]::PostMessage($hostw,[S4Win.N]::TrayCallbackMessage,[IntPtr]::new([S4Win.N]::TrayIconId),[IntPtr]::new([S4Win.N]::WmRButtonUp))
    Start-Sleep -Milliseconds 600
    $m=Find-MenuWindow $pidv
    Write-Host ("[D2] menu window={0}" -f $m)
    if ($m -eq [IntPtr]::Zero) { Write-Host "[D2] ERROR: menu did not open"; throw "no menu" }

    Nav $m 6
    Start-Sleep -Milliseconds 200
    $stillOpen = ((Find-MenuWindow $pidv) -ne [IntPtr]::Zero)
    Write-Host ("[D2] after 4x Down, menu still open={0}" -f $stillOpen)

    # Method A: PostMessage Enter lParam 0
    Send-MenuKey $m ([S4Win.N]::VkReturn)
    Start-Sleep -Milliseconds 800
    $closedA = ((Find-MenuWindow $pidv) -eq [IntPtr]::Zero)
    $sideA = (Find-WindowByTitle "DeskLanes 设置") -ne [IntPtr]::Zero
    Write-Host ("[D2] MethodA(PostMessage Enter): menu closed={0} settings appeared={1}" -f $closedA,$sideA)
    [S4Win.N]::Wins.Clear()
    [void][S4Win.N]::EnumWindows({param($h,$l) try { [S4Win.N]::Wins.Add([S4Win.N]::DescribeWin($h)) } catch {} return $true }, [IntPtr]::Zero)
    $allDs=@(); foreach($w in [S4Win.N]::Wins){ $pp=$w.Split('|'); if($pp[4] -eq [string]$pidv -and $pp[2] -ne ""){ $allDs += $w } }
    Write-Host ("[D2] windows of app after invoke: {0}" -f ($allDs -join " || "))
    $resultA = if($closedA -and $sideA){"INVOKED+SWITCHED"} elseif($closedA){"INVOKED(no switch?)"} else {"NOT INVOKED"}

    # need to re-open for method C if not yet proven
    if ($resultA -ne "INVOKED+SWITCHED") {
        # close any menu
        [S4Win.N]::keybd_event([S4Win.N]::VkEscape,0,0,[IntPtr]::Zero); [S4Win.N]::keybd_event([S4Win.N]::VkEscape,0,2,[IntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        $hostw=Find-Host $pidv
        [void][S4Win.N]::PostMessage($hostw,[S4Win.N]::TrayCallbackMessage,[IntPtr]::new([S4Win.N]::TrayIconId),[IntPtr]::new([S4Win.N]::WmRButtonUp))
        Start-Sleep -Milliseconds 600
        $m=Find-MenuWindow $pidv
        if ($m -ne [IntPtr]::Zero) {
            Nav $m 6; Start-Sleep -Milliseconds 200
            # Method C: SetForegroundWindow + keybd_event Enter
            [void][S4Win.N]::SetForegroundWindow($m)
            Start-Sleep -Milliseconds 120
            [S4Win.N]::keybd_event([S4Win.N]::VkReturn,0,0,[IntPtr]::Zero)
            [S4Win.N]::keybd_event([S4Win.N]::VkReturn,0,2,[IntPtr]::Zero)
            Start-Sleep -Milliseconds 800
            $closedC = ((Find-MenuWindow $pidv) -eq [IntPtr]::Zero)
            $sideC = (Find-WindowByTitle "DeskLanes 设置") -ne [IntPtr]::Zero
            Write-Host ("[D2] MethodC(SetForeground+keybd Enter): menu closed={0} sidebar appeared={1}" -f $closedC,$sideC)
        }
    }
    Write-Host ("[D2] RESULT MethodA: {0}" -f $resultA)
} finally {
    Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    if ($backup -and (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $backup -Destination $settingsFile -Force }
    Write-Host "[D2] cleanup done"
}

