<#
.SYNOPSIS
  Removes Stratoscope for the current Windows user: the running app exits, then its shortcuts, registry entry and files go.
.PARAMETER RemoveSettings
  Also delete config.json and error.log. Without it they stay, so a reinstall keeps your servers.
.PARAMETER Quiet
  No confirmation question.
#>
[CmdletBinding()]
param(
    [switch]$RemoveSettings,
    [switch]$Quiet,
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Stratoscope')
)

$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($InstallDirectory)
$exe = Join-Path $target 'Stratoscope.exe'

if (-not $Quiet -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
    $what = if ($RemoveSettings) { 'the app and your settings' } else { 'the app (your settings stay)' }
    $answer = Read-Host "Remove Stratoscope: $what? [y/N]"
    if ($answer -notmatch '^(y|yes|j|ja)$') { Write-Host 'Nothing removed.'; return }
}

if (Test-Path -LiteralPath $exe) {
    [void](Start-Process -FilePath $exe -ArgumentList '--quit' -Wait -PassThru -WindowStyle Hidden)   # exit code 1 only means nothing was running
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'Stratoscope' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0) {
        Start-Sleep -Milliseconds 200
    }
    # A copy that did not quit in time is ended: the app holds no unsaved work.
    Get-Process -Name 'Stratoscope' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}

foreach ($link in (Join-Path ([Environment]::GetFolderPath('Programs')) 'Stratoscope.lnk'), (Join-Path ([Environment]::GetFolderPath('Startup')) 'Stratoscope.lnk'), (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Stratoscope.lnk')) {
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
}
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Stratoscope'
if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }

if (Test-Path -LiteralPath $target) {
    $keep = if ($RemoveSettings) { @() } else { @('config.json', 'error.log') }
    # The running uninstall.ps1 may live in this folder: it is removed last by a short-lived cmd, never by deleting itself in place.
    Get-ChildItem -LiteralPath $target -Force | Where-Object { $keep -notcontains $_.Name -and $_.FullName -ne $PSCommandPath } | Remove-Item -Recurse -Force
    if ($PSCommandPath -and $PSCommandPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
        # rmdir only succeeds when the folder is empty, so kept settings keep their folder.
        $cleanup = 'ping 127.0.0.1 -n 3 >nul & del /q "' + $PSCommandPath + '" & rmdir "' + $target + '"'
        Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $cleanup) -WindowStyle Hidden
    }
    elseif (Test-Path -LiteralPath $target) {
        if ((Get-ChildItem -LiteralPath $target -Force).Count -eq 0) { Remove-Item -LiteralPath $target -Force }
    }
}
Write-Host 'Stratoscope was removed.'
