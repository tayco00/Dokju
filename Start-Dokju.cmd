@echo off
setlocal
if not exist "%~dp0bin\Dokju.Start.exe" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "%~dp0bin\Dokju.Start.exe"
