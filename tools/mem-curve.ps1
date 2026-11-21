# Release memory curve: startup -> mode switches -> idle, sampling WorkingSet
param(
    [string]$Exe = "C:\Users\TTjustTT\Documents\Github\DeskLanes\publish\win-x64-selfcontained\DeskLanes.exe"
)

function Sample([string]$label) {
    $p = Get-Process DeskLanes -ErrorAction SilentlyContinue
    if ($p) {
        $mb = [math]::Round(($p | Measure-Object WorkingSet64 -Sum).Sum / 1MB, 1)
        Write-Output ("{0,-14} {1,8} MB" -f $label, $mb)
    } else {
        Write-Output ("{0,-14} {1,8}" -f $label, "N/A")
    }
}

Get-Process DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Start-Process -FilePath $Exe
Start-Sleep -Seconds 5
Sample "startup+5s"
Start-Sleep -Seconds 10
Sample "idle+15s"

Add-Type -AssemblyName System.Windows.Forms
for ($i = 1; $i -le 6; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait("^% ")
    Start-Sleep -Milliseconds 1200
    Sample "toggle$i"
}
Start-Sleep -Seconds 10
Sample "idle-final"
