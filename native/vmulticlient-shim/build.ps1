#Requires -Version 5.1
<#
.SYNOPSIS
  Builds vmulticlient.dll from the vendored vmulti-win11 client source.

.DESCRIPTION
  GlassLink calls vmulticlient.dll through P/Invoke (Cdecl). Upstream ships the
  vmulti client as a static .lib, so this script compiles it as an x64 DLL with a
  stable C export table. Requires Visual Studio's MSVC x64 toolchain.

  The vmulti kernel driver itself is built and installed separately from
  xaviersykora/vmulti-win11. See docs/DRIVERS.md.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$vendor = Join-Path $root 'vendor'
$outDir = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio 2022/2026 with 'Desktop development with C++'."
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw "MSVC C++ build tools were not found. Install 'Desktop development with C++' in Visual Studio."
}

$vcvars = if ($Architecture -eq 'x64') {
    Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
} else {
    Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsarm64.bat'
}
if (-not (Test-Path $vcvars)) {
    throw "vcvars script not found: $vcvars"
}

$source = Join-Path $vendor 'client.c'
$def = Join-Path $root 'vmulticlient.def'
$out = Join-Path $outDir 'vmulticlient.dll'
$obj = Join-Path $outDir 'client.obj'
$lib = Join-Path $outDir 'vmulticlient.lib'

$command = @(
    "call `"$vcvars`" >nul",
    "cl /nologo /O2 /MT /LD /D_CRT_SECURE_NO_WARNINGS /I`"$vendor`" `"$source`" /Fe:`"$out`" /Fo:`"$obj`" /link /DEF:`"$def`" setupapi.lib hid.lib /OUT:`"$out`" /IMPLIB:`"$lib`"",
    "exit /b %errorlevel%"
) -join ' && '

Write-Host "Building vmulticlient.dll ($Architecture, $Configuration)..." -ForegroundColor Cyan
cmd.exe /d /c $command
if ($LASTEXITCODE -ne 0) {
    throw "vmulticlient.dll build failed (exit $LASTEXITCODE)."
}

$signature = Get-AuthenticodeSignature $out
if ($signature.Status -eq 'NotSigned') {
    Write-Warning "vmulticlient.dll is unsigned. This is normal for a local development build; Windows may show a SmartScreen prompt."
}
Write-Host "Built: $out" -ForegroundColor Green
Write-Host "GlassLink copies native\vmulticlient-shim\bin\vmulticlient.dll into its output folder on build." -ForegroundColor DarkGray
