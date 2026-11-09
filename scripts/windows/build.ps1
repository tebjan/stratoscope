<#
.SYNOPSIS
  Tests and builds Stratoscope for Windows: Sources\Windows\artifacts\Stratoscope-<version>-windows.zip (and a framework-dependent zip) and Stratoscope-<version>-windows-setup.exe.
.DESCRIPTION
  Needs the .NET SDK 10 (see Sources\Windows\global.json) and Windows. Everything is `dotnet test` and `dotnet publish`; packages come from
  nuget.org through the project files. The zips hold the published output plus install.cmd, install.ps1 and uninstall.ps1.
.EXAMPLE
  .\scripts\windows\build.ps1 -Version 0.3.1
#>
[CmdletBinding()]
param(
    [string]$Version = '0.3.1',
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
    $project = 'StratoscopeWindows.csproj'
    $publish = Join-Path $windows 'artifacts/publish'
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

    # The release asset: one compressed file, nothing to install (single file and compression are set in the project for a self-contained
    # publish; the smallest option WinForms supports, see docs/WINDOWS.md).
    dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained -p:Version=$Version -o (Join-Path $publish 'self-contained') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The self-contained publish failed.' }
    # The small one: needs the .NET 10 Desktop Runtime.
    dotnet publish $project -c Release -r $RuntimeIdentifier --no-self-contained -p:Version=$Version -o (Join-Path $publish 'framework-dependent') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The framework-dependent publish failed.' }

    $scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Name -notin 'build.ps1', 'screenshots.ps1', 'stratoscope.iss', '.gitattributes' })
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
        Write-Host ('{0}  {1:N1} MB' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
    }

    # The installer users download: one Setup.exe (scripts\windows\stratoscope.iss, built with Inno Setup).
    $iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup was not found. Install it once: winget install --id JRSoftware.InnoSetup' }
    & $iscc "/DAppVersion=$Version" ("/DSourceDir=" + (Join-Path $publish 'self-contained')) '/Q' (Join-Path $PSScriptRoot 'stratoscope.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
    $setup = Join-Path $windows "artifacts/Stratoscope-$Version-windows-setup.exe"
    Write-Host ('{0}  {1:N1} MB' -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
}
finally { Pop-Location }
