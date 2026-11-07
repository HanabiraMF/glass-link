#Requires -Version 5.1
<#
.SYNOPSIS
  Builds GlassLink and the vmulti client DLL.

.PARAMETER Configuration
  Debug or Release (default Release).

.PARAMETER SkipNative
  Skip building vmulticlient.dll (useful if only software input layer is needed).

.PARAMETER Run
  Start GlassLink after a successful build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipNative,
    [switch]$Run,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK not found. Install .NET 10 SDK from https://dotnet.microsoft.com/'
    }

    $sdkVersion = (dotnet --version) 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet --version failed. Check the .NET installation.'
    }
    Write-Host "Using .NET SDK $sdkVersion" -ForegroundColor Cyan
    if (-not $sdkVersion.StartsWith('10.')) {
        Write-Warning 'This project targets .NET 10. The active SDK is not 10.x; build may fail.'
    }

    $nativeDll = Join-Path $root 'native\vmulticlient-shim\bin\vmulticlient.dll'
    if (-not $SkipNative -and -not (Test-Path $nativeDll)) {
        Write-Host 'Building vmulticlient.dll...' -ForegroundColor Cyan
        try {
            & (Join-Path $root 'native\vmulticlient-shim\build.ps1')
        }
        catch {
            Write-Warning "vmulticlient.dll was not built: $($_.Exception.Message)"
            Write-Warning 'GlassLink will still build; the virtual hardware layer needs vmulticlient.dll and Interception.'
        }
    }

    Write-Host "Building GlassLink ($Configuration)..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'GlassLink.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }

    Write-Host ''
    Write-Host 'Build succeeded.' -ForegroundColor Green
    Write-Host "Run: dotnet run --project src\GlassLink\GlassLink.csproj -c $Configuration -- --controller=auto"
    Write-Host 'Or:  powershell -NoProfile -ExecutionPolicy Bypass -File run.ps1'

    if ($Run) {
        & (Join-Path $root 'run.ps1') -Configuration $Configuration -NoBuild @AppArgs
    }
}
finally {
    Pop-Location
}
