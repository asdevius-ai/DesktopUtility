<#
.SYNOPSIS
    Builds DesktopUtility for release.

.DESCRIPTION
    1. Publishes the app (self-contained, single-file, win-x64).
    2. Assembles a portable folder with everything needed to run WITHOUT installing:
         dist\DesktopUtility\  (exe, FFmpeg DLLs, wwwroot, VirtualDisplayDriver)  + dist\DesktopUtility-portable.zip
    3. Compiles the Inno Setup installer (Output\DesktopUtility_Setup.exe) if Inno Setup 6 is installed.

.PARAMETER PortableOnly
    Skip the installer step.

.EXAMPLE
    .\build_release.ps1 -PortableOnly
#>
[CmdletBinding()]
param(
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$projectDir = Join-Path $root 'src\DesktopUtility.Host'
$publishDir = Join-Path $projectDir 'bin\Release\net8.0-windows\win-x64\publish'
$distDir    = Join-Path $root 'dist\DesktopUtility'
$driverDir  = Join-Path $root 'scripts\usbmmidd_v2\usbmmidd_v2'

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Find-Dotnet {
    # A machine can have a runtime-only dotnet on PATH, so check that the candidate actually has an SDK.
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^8\.') { return $candidate }
    }
    throw ".NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

function Find-InnoSetup {
    $candidates = @(
        $env:INNO_SETUP_COMPILER,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    if ($candidates) { return @($candidates)[0] }
    return $null
}

# ── 1. Publish ───────────────────────────────────────────────
Write-Step 'Publishing application (Release, win-x64)'
$dotnet = Find-Dotnet
& $dotnet publish (Join-Path $projectDir 'DesktopUtility.Host.csproj') -c Release -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# ── 2. Portable folder ───────────────────────────────────────
Write-Step 'Assembling portable folder'
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir | Out-Null

Copy-Item (Join-Path $publishDir '*') $distDir -Recurse -Force
Remove-Item (Join-Path $distDir 'web.config') -ErrorAction SilentlyContinue   # IIS leftover, not used by the desktop host
if (Test-Path $driverDir) {
    Copy-Item $driverDir (Join-Path $distDir 'VirtualDisplayDriver') -Recurse -Force
}
else {
    Write-Warning "Virtual display driver not found at $driverDir - second-monitor mode will not work."
}

$zip = Join-Path $root 'dist\DesktopUtility-portable.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zip
Write-Host "Portable build: $distDir"
Write-Host "Portable zip:   $zip"

# ── 3. Installer ─────────────────────────────────────────────
if ($PortableOnly) { return }

Write-Step 'Compiling installer'
$iscc = Find-InnoSetup
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found - skipping the installer. Install it from https://jrsoftware.org/isinfo.php (or set INNO_SETUP_COMPILER).'
    return
}

Push-Location $root
try {
    & $iscc 'installer.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
}
finally {
    Pop-Location
}

Write-Host "`nInstaller: $(Join-Path $root 'Output\DesktopUtility_Setup.exe')" -ForegroundColor Green
