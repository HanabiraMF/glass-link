#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Enables Windows test signing so the self-signed vmulti-win11 kernel driver can load.

.DESCRIPTION
  Must be run in an elevated PowerShell. A reboot is required. If Secure Boot is
  enabled, test signing may be rejected by firmware; disable Secure Boot first or
  use a properly signed production driver.

.PARAMETER Reboot
  Reboot immediately after enabling test signing.
#>
[CmdletBinding()]
param([switch]$Reboot)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

Write-Host 'Current boot configuration:' -ForegroundColor Cyan
bcdedit /enum '{current}' | Select-String -Pattern 'testsigning|nointegritychecks|secureboot'

$secureBoot = try { Confirm-SecureBootUEFI -ErrorAction Stop } catch { $null }
if ($secureBoot -eq $true) {
    Write-Warning 'Secure Boot is enabled. Test-signed vmulti driver may not load. Disable Secure Boot in firmware settings or use a production-signed driver.'
}

bcdedit /set testsigning on | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'bcdedit failed to enable testsigning.' }
bcdedit /set nointegritychecks off | Out-Null

Write-Host ''
Write-Host 'Test signing is enabled. Reboot Windows before installing vmulti-win11.' -ForegroundColor Green
Write-Host 'After reboot, run: scripts\install-vmulti.ps1 -DriverDist <path-to-vmulti-win11-driver_dist\x64>'
Write-Host 'To return to normal mode later: bcdedit /set testsigning off, then reboot.'

if ($Reboot) {
    Restart-Computer -Force
}
