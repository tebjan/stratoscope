<#
.SYNOPSIS
  Regenerates every Windows image in docs\img. No arguments needed.
.DESCRIPTION
  Builds the development tools if they are not built yet, then renders all images offscreen from the demo engine with neutral server names and a fixed
  clock: no engine, no network, no window on your desktop, the same pixels on every run. Takes a few seconds.
.PARAMETER Only
  Render only these images (names without "windows-" and ".png"). Default: panel-dark, panel-light and readout-states, the ones the docs show. All names: panel-dark, panel-light, panel-tooltips, panel-offline,
  panel-no-model, panel-parallel-dark, panel-parallel-light, panel-parallel3-dark, panel-parallel3-light, readout-states, readout-scales, readout-slots, readout-numbers, readout-glyph-sizes, readout-layouts.
.PARAMETER Output
  Where the PNGs go. Default: docs\img.
.PARAMETER Dpi
  The display scales of the readout-scales image as DPI values. Default: 96, 120, 144, 168, 192, 240 (100 to 250 %).
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [string]$Output,
    [int[]]$Dpi
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Output) { $Output = Join-Path $root 'docs/img' }
$exe = Join-Path $root 'Sources/Windows/artifacts/bin/StratoscopeTools/release/StratoscopeTools.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host 'Building the tools (dotnet build -c Release) ...'
    Push-Location (Join-Path $root 'Sources/Windows')
    try {
        & dotnet build 'StratoscopeTools.csproj' -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    }
    finally { Pop-Location }
}

$arguments = @('--render-images', $Output)
if (-not $Only) { $Only = @('panel-dark', 'panel-light', 'readout-states') }   # the images README.md and docs/WINDOWS.md show
$arguments += @('--only', ($Only -join ','))
if ($Dpi) { $arguments += @('--dpi', ($Dpi -join ',')) }
$quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$process = Start-Process -FilePath $exe -ArgumentList $quoted -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "StratoscopeTools.exe --render-images exited with $($process.ExitCode)." }
Get-ChildItem -LiteralPath $Output -Filter 'windows-*.png' | Select-Object Name, Length
