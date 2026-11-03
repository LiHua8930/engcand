@echo off
rem ============================================================
rem  English Candidate Assistant (EngCand) - launcher
rem  Double-click this file to start the tray application.
rem  Requires: dist\app\EngCand.exe (run build.ps1 first).
rem ============================================================
setlocal
cd /d "%~dp0dist\app"
if not exist "EngCand.exe" (
    echo.
    echo [ERROR] EngCand.exe not found in "%~dp0dist\app".
    echo         Please run build.ps1 first:
    echo             powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
    echo.
    pause
    exit /b 1
)
start "" "EngCand.exe"
exit /b 0
