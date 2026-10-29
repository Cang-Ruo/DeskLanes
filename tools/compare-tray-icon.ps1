#Requires -Version 5.1
# 把浮层第一个图标与 DeskLanes 应用图标并排放大，便于比对
Add-Type -AssemblyName System.Drawing
$dir = "C:\Users\TTjustTT\Documents\Github\DeskLanes\docs\stage7-evidence"
$src = [System.Drawing.Image]::FromFile("$dir\tray-flyout-open-full.png")

# 浮层第一个图标在截图中的位置（由 tray-flyout-crop 推算回全图坐标）
$ix = 2090 + 25; $iy = 1060 + 155; $iw = 36; $ih = 36

$tile = New-Object System.Drawing.Bitmap(36, 36)
$g = [System.Drawing.Graphics]::FromImage($tile)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, 36, 36)),
    (New-Object System.Drawing.Rectangle($ix, $iy, $iw, $ih)),
    [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

# 画布：左 = 浮层图标，右 = 源 ico（256px 缩到 36 再放大，等价于图标本身的样子）
$canvas = New-Object System.Drawing.Bitmap((36 * 8 * 2 + 24), (36 * 8))
$bg = [System.Drawing.Brushes]::White
$g2 = [System.Drawing.Graphics]::FromImage($canvas)
$g2.FillRectangle($bg, 0, 0, $canvas.Width, $canvas.Height)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($tile, 0, 0, (36 * 8), (36 * 8))
$g2.DrawImage($src, (36 * 8 + 24), 0, (36 * 8), (36 * 8))  # 右侧：源图（占位）
$g2.Dispose()

# 右侧：应用图标（用此前 DrawIconEx 渲染好的 icon-from-source-ico-32.png，
# System.Drawing.Icon.ToBitmap() 解不了 PNG 压缩的 ico 条目，不能直接用）
$icoImg = [System.Drawing.Image]::FromFile("$dir\icon-from-source-ico-32.png")
$g3 = [System.Drawing.Graphics]::FromImage($canvas)
$g3.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g3.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g3.DrawImage($icoImg, (36 * 8 + 24), 0, (36 * 8), (36 * 8))
$g3.Dispose()
$icoImg.Dispose()

$canvas.Save("$dir\compare-tray-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
$tile.Dispose()
$src.Dispose()
Write-Output 'saved: compare-tray-icon.png（左=托盘浮层图标，右=应用图标）'
