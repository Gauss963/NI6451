<#
.SYNOPSIS
    Builds the x86-64 Windows executable for the USB-6451 WinUI application.

.DESCRIPTION
    Must be run on Windows. WinUI 3 cannot be cross-compiled: the Windows App SDK build
    chain (the XAML compiler, MIDL, the C#/WinRT projection generator and the Windows SDK
    build tools) ships as Windows PE binaries that MSBuild invokes directly, and there is
    no macOS or Linux host for them.

    Requirements:
      - Windows 10 1809 (build 17763) or newer, x64
      - .NET SDK 8.0 (https://dotnet.microsoft.com/download/dotnet/8.0)

    The result is an unpackaged, self-contained folder: Ni6451.exe next to the .NET runtime
    and the Windows App SDK, so the target machine needs nothing pre-installed except the
    NI-DAQmx driver (which supplies nicaiu.dll).

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER OutputPath
    Where to place the published output. Defaults to winui/artifacts/win-x64.

.EXAMPLE
    pwsh winui/build/build-win-x64.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$appProject = Join-Path $repoRoot 'winui/src/Ni6451.App/Ni6451.App.csproj'
$toolsProject = Join-Path $repoRoot 'winui/src/Ni6451.Tools/Ni6451.Tools.csproj'

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'winui/artifacts/win-x64'
}

if (-not $IsWindows) {
    throw 'WinUI 3 can only be built on Windows. See the note in winui/README.md.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install .NET SDK 8.0 and try again.'
}

Write-Host '==> Restoring' -ForegroundColor Cyan
dotnet restore (Join-Path $repoRoot 'winui/Ni6451.sln')

Write-Host '==> Running the hardware-independent self-test' -ForegroundColor Cyan
dotnet run --project $toolsProject -c $Configuration -- selftest
if ($LASTEXITCODE -ne 0) { throw "Self-test failed (exit code $LASTEXITCODE)." }

Write-Host "==> Publishing $Configuration | x64 to $OutputPath" -ForegroundColor Cyan
dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    -p:Platform=x64 `
    --self-contained true `
    -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $OutputPath 'Ni6451.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

Write-Host ''
Write-Host "Built: $exe" -ForegroundColor Green
Write-Host "Ship the whole $OutputPath folder; Ni6451.exe on its own will not start."
