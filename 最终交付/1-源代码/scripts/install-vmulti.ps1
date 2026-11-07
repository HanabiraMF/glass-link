#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the vmulti-win11 virtual multitouch HID driver and builds the client DLL.

.DESCRIPTION
  vmulti-win11 must be built separately with Visual Studio + WDK because it contains
  a kernel driver signed with a test certificate. This script:
    1. verifies test signing,
    2. trusts the WDK test certificate,
    3. installs vmulti.inf with pnputil,
    4. builds native\vmulticlient-shim\bin\vmulticlient.dll,
    5. prints the reboot and testvmulti.exe /multitouch verification steps.

.PARAMETER DriverDist
  Path to the x64 driver_dist folder containing vmulti.inf and vmulti.sys.
  Usually <vmulti-win11>\driver_dist\x64.

.PARAMETER CertPath
  Optional path to WDKTestCert.cer. Defaults to <DriverDist>\..\cert\WDKTestCert.cer.

.PARAMETER Reboot
  Reboot immediately after installation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DriverDist,
    [string]$CertPath,
    [switch]$Reboot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

$DriverDist = (Resolve-Path $DriverDist).Path
$inf = Join-Path $DriverDist 'vmulti.inf'
$sys = Join-Path $DriverDist 'vmulti.sys'
if (-not (Test-Path $inf)) { throw "vmulti.inf not found: $inf" }
if (-not (Test-Path $sys)) { throw "vmulti.sys not found: $sys. Build the vmulti-win11 driver first." }

$bootConfig = (bcdedit /enum '{current}' 2>&1 | Out-String)
$testSigning = $bootConfig -match '(?im)^\s*testsigning\s+(yes|on|是)\s*$'
if (-not $testSigning) {
    throw 'testsigning is not enabled (or its localized value was not recognized). Run scripts\enable-testsigning.ps1 as administrator and reboot first.'
}

if (-not $CertPath) {
    $CertPath = Join-Path (Split-Path -Parent $DriverDist) 'cert\WDKTestCert.cer'
    if (-not (Test-Path $CertPath)) {
        $CertPath = Join-Path (Split-Path -Parent (Split-Path -Parent $DriverDist)) 'cert\WDKTestCert.cer'
    }
}
if (-not (Test-Path $CertPath)) {
    throw "Test certificate not found. Pass -CertPath <WDKTestCert.cer>. Checked: $CertPath"
}

Write-Host "Installing test certificate: $CertPath" -ForegroundColor Cyan
Import-Certificate -FilePath $CertPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
Import-Certificate -FilePath $CertPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null

Write-Host "Installing vmulti driver: $inf" -ForegroundColor Cyan
pnputil /add-driver $inf /install | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "pnputil failed (exit $LASTEXITCODE). Check test signing, Secure Boot and certificate trust."
}

Write-Host 'Building vmulticlient.dll from vendored vmulti client source...' -ForegroundColor Cyan
& (Join-Path $repoRoot 'native\vmulticlient-shim\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'vmulticlient.dll build failed.' }

$nativeDir = Join-Path $repoRoot 'native'
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
Copy-Item (Join-Path $repoRoot 'native\vmulticlient-shim\bin\vmulticlient.dll') (Join-Path $nativeDir 'vmulticlient.dll') -Force
Write-Host "Client DLL copied to: $(Join-Path $nativeDir 'vmulticlient.dll')" -ForegroundColor Green

$testExe = Join-Path $DriverDist 'testvmulti.exe'
Write-Host ''
Write-Host 'Installation complete. Reboot Windows, then verify:' -ForegroundColor Green
Write-Host "  1. $testExe /multitouch"
Write-Host '     The test program moves the cursor and injects four simultaneous touch contacts.'
Write-Host '  2. Open Windows 设置 → 蓝牙和其他设备 → 触摸板/笔，确认出现 vmulti 触摸设备。'
Write-Host '  3. Use a Win32 touch message viewer (or Paint) and verify all four contact positions.'
Write-Host '  4. In GlassLink choose 虚拟硬件层 and test the mobile normal-mode multi-touch.'
Write-Host ''
Write-Host 'If vmulti multi-touch is unreliable on this Windows build, use the MultiTouchVista-compatible driver fallback described in docs\DRIVERS.md.'

if ($Reboot) {
    Restart-Computer -Force
}
