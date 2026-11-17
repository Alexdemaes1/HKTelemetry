@echo off
rem HKTelemetry - lanza scripts\install.ps1 (PowerShell, incluido en Windows).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
echo.
pause
