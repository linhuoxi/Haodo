@echo off
cd /d "%~dp0"
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build_only.ps1"
if %errorlevel% neq 0 (
    echo.
    echo Script failed with error code %errorlevel%.
    pause
)
