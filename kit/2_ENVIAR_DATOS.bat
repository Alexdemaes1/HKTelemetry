@echo off
rem HKTelemetry - lanza scripts\export.ps1 (PowerShell, incluido en Windows).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\export.ps1"
echo.
pause
