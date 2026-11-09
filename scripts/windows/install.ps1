<#
.SYNOPSIS
  Installs Stratoscope for the current Windows user. No administrator rights, nothing outside your profile.
.DESCRIPTION
  Copies the app to %LOCALAPPDATA%\Stratoscope, adds a Start menu shortcut, optionally starts it at logon (a shortcut in your
  Startup folder), lists it under Settings > Apps, and starts it. An existing settings file is never overwritten.
  Run it from the unpacked zip, or from a source checkout after scripts\windows\build.ps1.
.PARAMETER Autostart
  default: on for a new install, and a reinstall keeps your earlier choice. yes: start Stratoscope at logon. no: do not.
.PARAMETER DesktopIcon
  Also add a desktop shortcut. Off by default.
.PARAMETER NoStart
  Do not start the app after installing.
.PARAMETER InstallDirectory
  Where to install. Default: %LOCALAPPDATA%\Stratoscope
#>
[CmdletBinding()]
param(
    [ValidateSet('default', 'yes', 'no')][string]$Autostart = 'default',
    [switch]$DesktopIcon,
    [switch]$NoStart,
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Stratoscope')
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Stratoscope for Windows installs on Windows only.' }

# Where the files are: next to this script (the unpacked zip), or in the published output of a checkout (after scripts\windows\build.ps1).
$source = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $source 'Stratoscope.exe'))) {
    $source = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Sources/Windows/artifacts/publish/self-contained'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'Stratoscope.exe'))) { throw 'Stratoscope.exe not found next to install.ps1. Run scripts\windows\build.ps1 first.' }

$target = [IO.Path]::GetFullPath($InstallDirectory)
$exe = Join-Path $target 'Stratoscope.exe'

# A running copy of this install exits through its own quit signal first.
if (Test-Path -LiteralPath $exe) {
    # The exe is a Windows GUI program: only Start-Process -Wait gives its exit code (0: the running app was asked to quit).
    $quit = Start-Process -FilePath $exe -ArgumentList '--quit' -Wait -PassThru -WindowStyle Hidden
    if ($quit.ExitCode -eq 0) {
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'Stratoscope' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0) {
            Start-Sleep -Milliseconds 200
        }
    }
    # A copy that did not quit in time is ended: the app holds no unsaved work, and a copy left running would keep the new one from starting.
    Get-Process -Name 'Stratoscope' -ErrorAction SilentlyContinue | Stop-Process -Force
}

# The payload is whatever `dotnet publish` produced (one exe, or the exe with its runtime files), plus the uninstaller.
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force -Exclude 'install.ps1', 'install.cmd'
if ($source -ne $PSScriptRoot) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $target -Force }

function New-Shortcut([string]$path, [string]$description) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($path)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $target
        $link.Description = $description
        $link.IconLocation = "$exe,0"
        $link.Save()
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

$programs = [Environment]::GetFolderPath('Programs')
$startup = [Environment]::GetFolderPath('Startup')
New-Shortcut (Join-Path $programs 'Stratoscope.lnk') 'A live gauge for your local LLM engine'
if ($DesktopIcon) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Stratoscope.lnk') 'A live gauge for your local LLM engine' }

# Start with Windows: on by default for a new install; a reinstall keeps the earlier choice (the shortcut is there or it is not).
# -Autostart yes or no decides explicitly. The app also has a "Start with Windows" switch in its Settings menu.
$startupLink = Join-Path $startup 'Stratoscope.lnk'
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$setupKey = "$uninstallRoot\{BED3E514-C20B-4074-9E13-CBEACBE2ADD4}_is1"   # the entry of the Setup.exe (scripts\windows\stratoscope.iss)
$previous = (Test-Path -LiteralPath "$uninstallRoot\Stratoscope") -or (Test-Path -LiteralPath $setupKey)
$choice = switch ($Autostart) { 'yes' { $true } 'no' { $false } default { if ($previous) { Test-Path -LiteralPath $startupLink } else { $true } } }
if ($choice) { New-Shortcut $startupLink 'Stratoscope, started at logon' }
elseif (Test-Path -LiteralPath $startupLink) { Remove-Item -LiteralPath $startupLink -Force }

# An entry in Settings > Apps, so it can be removed like any other app. Per user: HKCU only. When the Setup.exe installed the app,
# its entry is the one in the list: there is only ever one.
if (-not (Test-Path -LiteralPath $setupKey)) {
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $key = "$uninstallRoot\Stratoscope"
    New-Item -Path $key -Force | Out-Null
    $uninstall = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $target 'uninstall.ps1') + '"'
    $values = @{
        DisplayName = 'Stratoscope'; DisplayVersion = "$version"; Publisher = 'Stratoscope contributors'; InstallLocation = $target
        DisplayIcon = "$exe,0"; UninstallString = $uninstall; QuietUninstallString = "$uninstall -Quiet"; URLInfoAbout = 'https://github.com/yosephyne/stratoscope'
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
}

if (-not $NoStart) { Start-Process -FilePath $exe -WindowStyle Hidden }

[pscustomobject]@{
    Executable      = $exe
    Settings        = Join-Path $target 'config.json'
    StartMenu       = Join-Path $programs 'Stratoscope.lnk'
    StartAtLogon    = (Test-Path -LiteralPath $startupLink)
    Started         = -not $NoStart
}
