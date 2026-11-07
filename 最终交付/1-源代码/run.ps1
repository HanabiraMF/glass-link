#Requires -Version 5.1
<#
.SYNOPSIS
  Runs GlassLink with the given command-line options.

.EXAMPLE
  powershell -File run.ps1 --controller=auto --fps=60 --bitrate=16000000

.EXAMPLE
  powershell -File run.ps1 --controller=software --no-https
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\GlassLink\GlassLink.csproj'
$nativeDll = Join-Path $root 'native\vmulticlient-shim\bin\vmulticlient.dll'

if (-not (Test-Path $nativeDll) -and -not $NoBuild) {
    Write-Host 'Building vmulticlient.dll...' -ForegroundColor Cyan
    & (Join-Path $root 'native\vmulticlient-shim\build.ps1')
}

$arguments = @('run', '--project', $project, '-c', $Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += '--'
$arguments += $AppArgs

Push-Location $root
try {
    & dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
