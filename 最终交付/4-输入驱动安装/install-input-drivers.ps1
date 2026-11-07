#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs GlassLink's optional hardware input drivers.

.DESCRIPTION
  Interception provides keyboard/mouse injection; vmulti-win11 provides a virtual
  multi-touch HID device. Both require administrator rights and a reboot.

  This script performs detection first:
    * Interception: downloads the official release if no local installer exists,
      installs the kernel filter driver, and copies x64 interception.dll next to
      GlassLink.
    * vmulti-win11: installs from drivers\payload\vmulti-win11 if present, or
      explains how to build/place the driver payload. vmulti has no official
      prebuilt Windows 11 binary, so it cannot be safely downloaded automatically.

  GlassLink falls back to its software SendInput layer if anything fails.
#>
[CmdletBinding()]
param(
    [switch]$SkipInterception,
    [switch]$SkipVMulti,
    [switch]$Reboot,
    [switch]$Stage2,
    [string]$AppDir
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$appDir = if ($AppDir) { $AppDir.Trim('"') } else { Split-Path $scriptDir -Parent }
$nativeDir = Join-Path $appDir 'native'
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw 'This installer must be run as administrator. Right-click install-input-drivers.cmd and choose "Run as administrator".'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Step 'System state'
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "OS: $($os.Caption) build $($os.BuildNumber) $($os.OSArchitecture)"

$secureBoot = try { Confirm-SecureBootUEFI -ErrorAction Stop } catch { $null }
if ($secureBoot -eq $true) {
    Write-Warning 'Secure Boot is enabled. The test-signed vmulti driver may be rejected. Disable Secure Boot or use a production-signed driver.'
}

# Both bundled drivers are Microsoft-signed, so test signing is not required.
# Do not parse localized bcdedit output here: Windows PowerShell 5.1 reads
# BOM-less UTF-8 as ANSI and can corrupt Chinese characters in regexes.
if (-not $Stage2) {
    Write-Host 'Bundled vmulti/Pentablet and Interception are installed directly; test signing is optional.'
}

# ---------------------------------------------------------------- Interception
if (-not $SkipInterception) {
    Write-Step 'Interception driver'
    $interceptionDll = Join-Path $nativeDir 'interception.dll'
    $service = sc.exe query interception 2>&1
    $installed = $LASTEXITCODE -eq 0

    if ($installed -and (Test-Path $interceptionDll)) {
        Write-Host 'Interception service and interception.dll already present.' -ForegroundColor Green
    }
    else {
        $localInstaller = Get-ChildItem -Path (Join-Path $scriptDir 'payload\Interception') -Recurse -Filter 'install-interception.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $localDll = Get-ChildItem -Path (Join-Path $scriptDir 'payload\Interception') -Recurse -Filter 'interception.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
        if (-not $localDll) {
            $localDll = Get-ChildItem -Path (Join-Path $scriptDir 'payload\Interception') -Recurse -Filter 'interception.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
        }

        if (-not $localInstaller -or -not $localDll) {
            Write-Host 'No local Interception payload. Downloading the latest official release...'
            try {
                $headers = @{ 'User-Agent' = 'GlassLink-Installer' }
                $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/oblitum/Interception/releases/latest' -Headers $headers
                $asset = $release.assets | Where-Object { $_.name -match '\.zip$' } | Select-Object -First 1
                if (-not $asset) { throw 'No ZIP asset in the Interception release.' }
                $zip = Join-Path $env:TEMP $asset.name
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $headers -UseBasicParsing
                $extract = Join-Path $env:TEMP ('glasslink-interception-' + [Guid]::NewGuid().ToString('N'))
                Expand-Archive -Path $zip -DestinationPath $extract -Force
                $localInstaller = Get-ChildItem -Path $extract -Recurse -Filter 'install-interception.exe' | Select-Object -First 1
                $localDll = Get-ChildItem -Path $extract -Recurse -Filter 'interception.dll' |
                    Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
                if (-not $localDll) { $localDll = Get-ChildItem -Path $extract -Recurse -Filter 'interception.dll' | Select-Object -First 1 }
            }
            catch {
                Write-Warning "Interception download/install failed: $($_.Exception.Message)"
                Write-Warning 'GlassLink will continue to work with the software input layer.'
            }
        }

        if ($localInstaller) {
            Write-Host 'Running the official Interception installer...'
            Push-Location $localInstaller.DirectoryName
            try { & $localInstaller.FullName /install | Out-Host } finally { Pop-Location }
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Interception installer returned $LASTEXITCODE. You can rerun it manually as administrator."
            }
            $installed = $true
        }

        if ($localDll) {
            Copy-Item $localDll.FullName $interceptionDll -Force
            Write-Host "Copied interception.dll -> $interceptionDll" -ForegroundColor Green
        }
        else {
            Write-Warning 'interception.dll was not found. The hardware layer will fall back to software until it is placed in the native folder.'
        }
    }
}

# --------------------------------------------------------------------- vmulti
if (-not $SkipVMulti) {
    Write-Step 'vmulti-win11 virtual multi-touch driver'
    $vmultiCandidates = @(
        (Join-Path $scriptDir 'payload\vmulti-win11\driver_dist'),
        (Join-Path $scriptDir 'payload\vmulti-win11'),
        (Join-Path $scriptDir 'vmulti-win11\driver_dist'),
        (Join-Path $scriptDir 'vmulti-win11')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $vmultiCandidates) {
        Write-Warning 'No vmulti-win11 driver payload is bundled in drivers\payload\vmulti-win11.'
        Write-Host 'vmulti-win11 has no official prebuilt binary. Build it with Visual Studio + WDK, then place:' -ForegroundColor Yellow
        Write-Host '  driver_dist\x64\vmulti.inf, vmulti.sys, testvmulti.exe, vmulticlient.lib'
        Write-Host 'and the cert folder under drivers\payload\vmulti-win11, or install it manually.'
        Write-Host 'See docs\DRIVERS.md. The software layer remains active in the meantime.'
    }
    else {
        $inf = Get-ChildItem -Path $vmultiCandidates -Recurse -Filter 'vmulti.inf' | Select-Object -First 1
        $sys = if ($inf) { Get-ChildItem -Path $inf.DirectoryName -Filter 'vmulti.sys' | Select-Object -First 1 } else { $null }
        $cert = Get-ChildItem -Path $vmultiCandidates -Recurse -Filter 'WDKTestCert.cer' | Select-Object -First 1
        if (-not $inf -or -not $sys) {
            Write-Warning 'vmulti.inf/vmulti.sys not found in the payload. Skipping vmulti installation.'
        }
        else {
            if ($cert) {
                Write-Host "Trusting test certificate: $($cert.FullName)"
                Import-Certificate -FilePath $cert.FullName -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
                Import-Certificate -FilePath $cert.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
            }

            $cat = Get-ChildItem -Path $vmultiCandidates -Recurse -Filter '*.cat' -ErrorAction SilentlyContinue | Select-Object -First 1
            $signed = $cat -and (Get-AuthenticodeSignature $cat.FullName).Status -eq 'Valid'
            if (-not $signed) {
                Write-Warning 'vmulti catalog is not validly signed; testsigning and certificate trust are required.'
            }

            $devcon = Get-ChildItem -Path $vmultiCandidates -Recurse -Filter 'devcon.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            Write-Host "Installing vmulti driver: $($inf.FullName)"
            if ($devcon) {
                Push-Location $inf.DirectoryName
                try {
                    & $devcon.FullName install $inf.Name 'pentablet\hid' | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host 'Device may already exist; updating the existing device...'
                        & $devcon.FullName update $inf.Name 'pentablet\hid' | Out-Host
                    }
                }
                finally { Pop-Location }
            }
            else {
                pnputil /add-driver $inf.FullName /install | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "pnputil returned $LASTEXITCODE. Check test signing and Secure Boot."
                }
            }

            $clientDll = Get-ChildItem -Path $vmultiCandidates -Recurse -Filter 'vmulticlient.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($clientDll) {
                Copy-Item $clientDll.FullName (Join-Path $nativeDir 'vmulticlient.dll') -Force
                Write-Host 'Copied vmulticlient.dll to native\.' -ForegroundColor Green
            }
            else {
                $shim = Join-Path $appDir 'drivers\vmulticlient.dll'
                if (Test-Path $shim) { Copy-Item $shim (Join-Path $nativeDir 'vmulticlient.dll') -Force }
            }
        }
    }
}

Write-Step 'Summary'
$interceptionOk = (sc.exe query interception 2>&1) -and (Test-Path (Join-Path $nativeDir 'interception.dll'))
Write-Host "Interception service/DLL: $interceptionOk"
Write-Host "vmulticlient.dll: $(Test-Path (Join-Path $nativeDir 'vmulticlient.dll'))"
Write-Host "vmulti device: $([bool](Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'vmulti' }))"
Write-Host ''
Write-Host 'A restart is required after installing/enabling kernel drivers.' -ForegroundColor Yellow
Write-Host 'After restart, run testvmulti.exe /multitouch (from the vmulti payload) and then start GlassLink.'
Write-Host 'GlassLink automatically falls back to the software layer if the hardware layer is not ready.'

Unregister-ScheduledTask -TaskName 'GlassLinkInputDriversStage2' -Confirm:$false -ErrorAction SilentlyContinue

if ($Reboot -or $rebootRequired) {
    $answer = if ($Reboot) { 'R' } else { Read-Host 'Reboot now? [y/N]' }
    if ($answer -match '^(y|yes|r)$') { Restart-Computer -Force }
}
