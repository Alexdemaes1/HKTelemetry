@echo off
rem HKTelemetry - lanza scripts\uninstall.ps1 (PowerShell, incluido en Windows).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1"
echo.
pause
