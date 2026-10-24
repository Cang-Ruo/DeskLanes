#Requires -Version 5.1
# 从浮层全屏截图里裁出右下角浮层区域并 2x 最近邻放大
Add-Type -AssemblyName System.Drawing
$dir = "C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence"
$src = [System.Drawing.Image]::FromFile("$dir\tray-flyout-open-full.png")
Write-Output ("full: {0}x{1}" -f $src.Width, $src.Height)

$cw = 460; $ch = 320
$cx = $src.Width - 470
$cy = $src.Height - 380
$crop = New-Object System.Drawing.Bitmap($cw, $ch)
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.DrawImage($src,
    (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)),
    (New-Object System.Drawing.Rectangle($cx, $cy, $cw, $ch)),
    [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$crop.Save("$dir\tray-flyout-crop.png", [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()

$up = New-Object System.Drawing.Bitmap(($cw * 2), ($ch * 2))
$g2 = [System.Drawing.Graphics]::FromImage($up)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($crop, 0, 0, ($cw * 2), ($ch * 2))
$g2.Dispose()
$up.Save("$dir\tray-flyout-crop-2x.png", [System.Drawing.Imaging.ImageFormat]::Png)
$up.Dispose()
$src.Dispose()
Write-Output 'saved: tray-flyout-crop.png / tray-flyout-crop-2x.png'
