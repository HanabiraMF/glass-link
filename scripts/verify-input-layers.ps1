#Requires -Version 5.1
<#
.SYNOPSIS
  Prints a diagnostic report for GlassLink's software and virtual-hardware input layers.

.DESCRIPTION
  Checks test signing, driver services, native DLL presence, and optionally runs
  vmulti's interactive multitouch test. Run as administrator for complete output.
#>
[CmdletBinding()]
param(
    [string]$TestVMultiPath = ''
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appNative = Join-Path $repoRoot 'native'
$appOutput = Get-ChildItem -Path (Join-Path $repoRoot 'src\GlassLink\bin') -Recurse -Filter 'GlassLink.dll' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

function Section([string]$title) {
    Write-Host ''
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

Section 'Windows'
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture | Format-List

Section 'Test signing / Secure Boot'
bcdedit /enum '{current}' | Select-String -Pattern 'testsigning|nointegritychecks'
try { 'SecureBoot=' + (Confirm-SecureBootUEFI -ErrorAction Stop) } catch { 'SecureBoot=unknown/legacy BIOS' }

Section 'Interception'
$interceptionService = sc.exe query interception 2>&1
$interceptionService | Out-Host
$interceptionDll = @(
    (Join-Path $appNative 'interception\interception.dll'),
    (Join-Path $appNative 'interception.dll'),
    (Join-Path $repoRoot 'src\GlassLink\bin\Debug\net10.0-windows\win-x64\native\interception.dll')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($interceptionDll) {
    Write-Host "interception.dll: $interceptionDll" -ForegroundColor Green
    (Get-Item $interceptionDll).VersionInfo | Format-List FileVersion, ProductVersion, CompanyName
} else {
    Write-Warning 'interception.dll not found. Run scripts\install-interception.ps1.'
}

Section 'vmulti'
$vmultiDll = @(
    (Join-Path $appNative 'vmulticlient.dll'),
    (Join-Path $appNative 'vmulticlient-shim\bin\vmulticlient.dll')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($vmultiDll) {
    Write-Host "vmulticlient.dll: $vmultiDll" -ForegroundColor Green
} else {
    Write-Warning 'vmulticlient.dll not found. Run native\vmulticlient-shim\build.ps1.'
}
$vmultiDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.FriendlyName -match 'vmulti|Virtual Multiple|HID-compliant touch screen' -or $_.InstanceId -match '00FF.*BACC'
}
if ($vmultiDevices) {
    $vmultiDevices | Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -AutoSize
} else {
    Write-Warning 'No vmulti PnP device found. Install vmulti-win11 and reboot.'
}

Section 'HID touch-capable devices'
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match 'touch|vmulti' } |
    Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize

Section 'GlassLink build'
if ($appOutput) {
    Write-Host "Latest build: $($appOutput.FullName)" -ForegroundColor Green
} else {
    Write-Warning 'No GlassLink build found. Run "dotnet build GlassLink.sln".'
}

Section 'Multitouch coordinate verification'
if (-not $TestVMultiPath) {
    $candidates = Get-ChildItem -Path (Split-Path $repoRoot -Parent) -Recurse -Filter 'testvmulti.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 3
    if ($candidates) { $TestVMultiPath = $candidates[0].FullName }
}
if ($TestVMultiPath -and (Test-Path $TestVMultiPath)) {
    Write-Host "Running: $TestVMultiPath /multitouch" -ForegroundColor Cyan
    & $TestVMultiPath /multitouch
    Write-Host 'Expected: four synthetic touch contacts move across the desktop; no exception.' -ForegroundColor Yellow
    Write-Host 'Use Windows touch diagnostics / a Win32 WM_TOUCH logger to confirm contact coordinates 0..32767 are mapped to the virtual screen.' -ForegroundColor Yellow
} else {
    Write-Warning 'testvmulti.exe not found. Pass -TestVMultiPath <path>.'
}

Write-Host ''
Write-Host 'Done. To switch layers, use the PC page selector or launch with --controller=hardware|software|auto.' -ForegroundColor Green
