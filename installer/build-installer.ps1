#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes GlassLink and builds the Inno Setup installer.

.DESCRIPTION
  - publishes a self-contained win-x64 WinExe build
  - copies wwwroot, assets, docs, and the driver bundle
  - finds (or bootstraps) Inno Setup ISCC
  - compiles GlassLink-Setup-<version>.exe

  Optional driver payloads can be placed under installer\drivers\payload before
  running this script; they are copied into the installed app's drivers folder.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installerDir = $PSScriptRoot
$publishDir = Join-Path $installerDir 'publish'
$iss = Join-Path $installerDir 'GlassLink.iss'

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $installerDir '.tools\inno\ISCC.exe'),
        (Join-Path $installerDir '.tools\inno6\app\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

Push-Location $root
try {
    if (-not $SkipPublish) {
        Write-Host 'Publishing self-contained GlassLink...' -ForegroundColor Cyan
        if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
        dotnet publish (Join-Path $root 'src\GlassLink\GlassLink.csproj') `
            -c $Configuration `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:DebugType=None `
            -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    }

    if (-not (Test-Path (Join-Path $publishDir 'GlassLink.exe'))) {
        throw "Publish output not found: $publishDir\GlassLink.exe"
    }

    # Driver bundle and support docs are part of the installed payload.
    $publishDrivers = Join-Path $publishDir 'drivers'
    if (Test-Path $publishDrivers) { Remove-Item $publishDrivers -Recurse -Force }
    New-Item -ItemType Directory -Force $publishDrivers | Out-Null
    Copy-Item (Join-Path $installerDir 'drivers\*') $publishDrivers -Recurse -Force

    $optionalPayload = Join-Path $installerDir 'drivers\payload'
    if (Test-Path $optionalPayload) {
        Copy-Item $optionalPayload (Join-Path $publishDrivers 'payload') -Recurse -Force
    }

    New-Item -ItemType Directory -Force (Join-Path $publishDir 'docs') | Out-Null
    Copy-Item (Join-Path $root 'docs\DRIVERS.md') (Join-Path $publishDir 'docs') -Force
    Copy-Item (Join-Path $root 'README.md') $publishDir -Force
    Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $publishDir -Force

    # Sanity checks: WinExe, frontend, tray icon, native client if available.
    if (-not (Test-Path (Join-Path $publishDir 'wwwroot\index.html'))) { throw 'wwwroot assets missing from publish.' }
    if (-not (Test-Path (Join-Path $publishDir 'Assets\glasslink.ico'))) { throw 'tray icon missing from publish.' }

    $iscc = Find-Iscc
    if (-not $iscc) {
        $bundledSetup = Join-Path $installerDir 'innosetup.exe'
        if (Test-Path $bundledSetup) {
            $toolsDir = Join-Path $installerDir '.tools\inno'
            New-Item -ItemType Directory -Force $toolsDir | Out-Null
            Write-Host "Installing Inno Setup to $toolsDir..." -ForegroundColor Cyan
            $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$toolsDir")
            Start-Process -FilePath $bundledSetup -ArgumentList $args -Wait
            $iscc = Find-Iscc
        }
    }

    if (-not $iscc) {
        throw @'
Inno Setup ISCC.exe was not found.
Install Inno Setup 6/7, or place the official installer at installer\innosetup.exe.
Download: https://jrsoftware.org/isdl.php
'@
    }

    Write-Host "Compiling installer with $iscc" -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

    $output = Join-Path $installerDir "Output\GlassLink-Setup-$Version.exe"
    if (-not (Test-Path $output)) { throw "Installer output not found: $output" }
    $size = [Math]::Round((Get-Item $output).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Installer: $output ($size MiB)" -ForegroundColor Green
}
finally {
    Pop-Location
}
