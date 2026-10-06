$ErrorActionPreference = "Stop"

$url = "https://www.amyuni.com/downloads/usbmmidd_v2.zip"
$destZip = "$PSScriptRoot\usbmmidd_v2.zip"
$extractPath = "$PSScriptRoot\usbmmidd_v2"

Write-Host "Downloading Virtual Display Driver (usbmmidd_v2)..."
Invoke-WebRequest -Uri $url -OutFile $destZip

Write-Host "Extracting to $extractPath..."
if (Test-Path $extractPath) {
    Remove-Item -Recurse -Force $extractPath
}
Expand-Archive -Path $destZip -DestinationPath $extractPath

Write-Host "Cleaning up zip file..."
Remove-Item $destZip

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Driver downloaded successfully!" -ForegroundColor Green
Write-Host "To install the virtual display, run the following command"
Write-Host "in an ADMINISTRATOR PowerShell:" -ForegroundColor Yellow
Write-Host ""
Write-Host "cd '$extractPath'"
Write-Host ".\deviceinstaller64.exe install usbmmidd.inf usbmmidd"
Write-Host ".\deviceinstaller64.exe enableidd 1"
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "After running those commands, Windows will detect a new display."
Write-Host "You can then use the Display Switcher in the DesktopUtility app."
