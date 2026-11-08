<#
.SYNOPSIS
  Tests and builds Stratoscope for Windows: Sources\Windows\artifacts\Stratoscope-<version>-windows.zip (and a framework-dependent zip), each with a .sha256.
.DESCRIPTION
  Needs the .NET SDK 10 (see Sources\Windows\global.json) and Windows. Everything is `dotnet test` and `dotnet publish`; packages come from
  nuget.org through the project files. The zips hold the published output plus install.cmd, install.ps1 and uninstall.ps1.
.EXAMPLE
  .\scripts\windows\build.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.4',
    [switch]$SkipTests,
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$windows = Join-Path $root 'Sources/Windows'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'The .NET SDK was not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download.' }
$sdk = (& dotnet --version)
if ([version]($sdk -replace '-.*$', '') -lt [version]'10.0') { throw ".NET SDK $sdk found, but 10.0 or newer is needed (Sources\Windows\global.json). Install the .NET 10 SDK." }

Push-Location $windows
try {
    if (-not $SkipTests) {
        dotnet test Stratoscope.Windows.sln -c Release -p:Version=$Version --nologo
        if ($LASTEXITCODE -ne 0) { throw 'The tests failed.' }
    }
    $project = 'App/StratoscopeWindows.csproj'
    $publish = Join-Path $windows 'artifacts/publish'
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

    # The release asset: one compressed file, nothing to install (single file and compression are set in the project for a self-contained
    # publish; the smallest option WinForms supports, see docs/WINDOWS.md).
    dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained -p:Version=$Version -o (Join-Path $publish 'self-contained') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained publish failed.' }
    # The small one: needs the .NET 10 Desktop Runtime.
    dotnet publish $project -c Release -r $RuntimeIdentifier --no-self-contained -p:Version=$Version -o (Join-Path $publish 'framework-dependent') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The framework-dependent publish failed.' }

    $scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Name -notin 'build.ps1', 'screenshots.ps1' })
    foreach ($flavour in @(@{ Dir = 'self-contained'; Name = "Stratoscope-$Version-windows" }, @{ Dir = 'framework-dependent'; Name = "Stratoscope-$Version-windows-framework-dependent" })) {
        $stage = Join-Path $windows "artifacts/package/$($flavour.Name)"
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item -Path (Join-Path $publish "$($flavour.Dir)/*") -Destination $stage -Recurse
        Copy-Item -LiteralPath $scripts.FullName -Destination $stage
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
        $zip = Join-Path $windows "artifacts/$($flavour.Name).zip"
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$zip.sha256", "$hash  $($flavour.Name).zip`n", (New-Object Text.UTF8Encoding($false)))
        Write-Host ('{0}  {1:N1} MB  sha256 {2}' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB), $hash)
    }
}
finally { Pop-Location }
