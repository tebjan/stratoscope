@echo off
rem Double-click to install Stratoscope for your Windows user. Runs install.ps1 without changing your PowerShell settings.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
