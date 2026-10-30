#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$out = "$env:USERPROFILE\Documents\Github\DeskLanes\docs\stage5-evidence\verify-clean-desktop.png"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("saved: {0}" -f $out)
} finally {
    $g.Dispose(); $bmp.Dispose()
}
