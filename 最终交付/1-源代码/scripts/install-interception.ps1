#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Downloads and installs the Interception keyboard/mouse filter driver.

.DESCRIPTION
  Interception is required by GlassLink's virtual hardware input layer. This
  script fetches the latest official GitHub release (or accepts a local ZIP),
  copies interception.dll into native\interception, and runs the official
  installer. A reboot is normally required.

  Non-commercial use is covered by Interception's LGPL 3.0 license. Commercial
  use requires a license from the author. See docs\DRIVERS.md.

.PARAMETER ZipPath
  Use a locally downloaded Interception release ZIP instead of the network.

.PARAMETER SkipDriverInstall
  Only copy interception.dll; do not run install-interception.exe.

.PARAMETER Reboot
  Reboot immediately after installation.
#>
[CmdletBinding()]
param(
    [string]$ZipPath,
    [switch]$SkipDriverInstall,
    [switch]$Reboot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot 'native\interception'
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $ZipPath) {
    Write-Host 'Querying latest Interception release...' -ForegroundColor Cyan
    $headers = @{ 'User-Agent' = 'GlassLink-Installer' }
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/oblitum/Interception/releases/latest' -Headers $headers
    $asset = $release.assets | Where-Object { $_.name -match '\.zip$' } | Select-Object -First 1
    if (-not $asset) { throw 'No ZIP asset found in the latest Interception release.' }

    $ZipPath = Join-Path $env:TEMP $asset.name
    Write-Host "Downloading $($asset.browser_download_url)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $ZipPath -Headers $headers -UseBasicParsing
}

if (-not (Test-Path $ZipPath)) { throw "ZIP not found: $ZipPath" }

$extractDir = Join-Path $env:TEMP ('glasslink-interception-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -Path $ZipPath -DestinationPath $extractDir -Force

$installer = Get-ChildItem -Path $extractDir -Recurse -Filter 'install-interception.exe' | Select-Object -First 1
$dll = Get-ChildItem -Path $extractDir -Recurse -Filter 'interception.dll' |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
if (-not $dll) {
    $dll = Get-ChildItem -Path $extractDir -Recurse -Filter 'interception.dll' | Select-Object -First 1
}
if (-not $dll) { throw 'interception.dll was not found in the release ZIP.' }

Copy-Item $dll.FullName (Join-Path $nativeDir 'interception.dll') -Force
Write-Host "Copied interception.dll to native\interception." -ForegroundColor Green

if (-not $SkipDriverInstall) {
    if (-not $installer) { throw 'install-interception.exe was not found. Re-run with -SkipDriverInstall and install manually.' }
    Write-Host 'Running the official Interception driver installer...' -ForegroundColor Cyan
    Push-Location $installer.Directory.FullName
    try {
        & $installer.FullName /install | Out-Host
    }
    finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Interception installer returned exit code $LASTEXITCODE. Try running $($installer.FullName) /install manually as administrator."
    }
}

Write-Host ''
Write-Host 'Interception installation requested. Reboot Windows, then verify:' -ForegroundColor Green
Write-Host '  sc.exe query interception'
Write-Host '  native\interception\interception.dll must exist next to the app or in native\.'
Write-Host '  Start GlassLink; the PC page should show "Interception 设备 ID 学习完成".'
Write-Host ''
Write-Host 'Note: Interception is LGPL for non-commercial use. Commercial use needs a separate license.' -ForegroundColor DarkGray

if ($Reboot) {
    Restart-Computer -Force
}
