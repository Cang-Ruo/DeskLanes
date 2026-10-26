# 实机验证悬浮窗 / 侧边栏模式：临时改 WindowMode 与相关行为开关 → 启动 → 截图 → 恢复配置
# 用法：tmp-live-mode.ps1 -Mode <0|2> -Title <窗口标题> -OutPng <输出.png>
#   Mode 0 = Floating（悬浮窗），2 = Sidebar（侧边栏）
param(
    [Parameter(Mandatory = $true)][int]$Mode,
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$OutPng,
    [Parameter(Mandatory = $true)][string]$ExePath
)

$settingsPath = Join-Path $env:LOCALAPPDATA 'DeskSpace\settings.json'
$backupPath = Join-Path $env:TEMP 'DeskSpace-settings-backup-live-mode.json'
Copy-Item -LiteralPath $settingsPath -Destination $backupPath -Force

try {
    $json = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $json.WindowMode = $Mode
    if ($Mode -eq 0) {
        # 悬浮窗：截图期间不要失焦自动隐藏
        $json.FloatingHideOnDeactivate = $false
        Write-Output "已临时切换 WindowMode=0 (Floating) 并关闭失焦隐藏"
    }
    else {
        # 侧边栏：截图期间保持展开且不失焦收起
        $json.SidebarExpanded = $true
        $json.SidebarAutoCollapse = $false
        Write-Output "已临时切换 WindowMode=2 (Sidebar)，展开且关闭失焦收起"
    }
    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

    $proc = Start-Process -FilePath $ExePath -PassThru
    Write-Output ("启动 PID = " + $proc.Id)

    $code = @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
public class LiveShot3 {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr FindByPidAndTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            if (t.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void SaveShot(Rectangle rect, string path) {
        using (var bmp = new Bitmap(rect.Width, rect.Height)) {
            using (var g = Graphics.FromImage(bmp)) {
                g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@
    Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing
    [void][LiveShot3]::SetProcessDPIAware()

    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Milliseconds 700
        $hwnd = [LiveShot3]::FindByPidAndTitle([uint32]$proc.Id, $Title)
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Output ("没有找到标题为 " + $Title + " 的可见窗口")
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }

    Start-Sleep -Seconds 7   # 等扫描 / 图标渲染稳定
    $r = New-Object LiveShot3+RECT
    [void][LiveShot3]::GetWindowRect($hwnd, [ref]$r)
    $rect = New-Object System.Drawing.Rectangle($r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
    Write-Output ("窗口矩形(DPI感知): {0},{1} {2}x{3}" -f $r.Left, $r.Top, $rect.Width, $rect.Height)
    [LiveShot3]::SaveShot($rect, $OutPng)
    Write-Output ("已保存: " + $OutPng)
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Output "应用已退出"
}
finally {
    if (Test-Path -LiteralPath $backupPath) {
        Copy-Item -LiteralPath $backupPath -Destination $settingsPath -Force
        Write-Output "配置已恢复"
    }
}
