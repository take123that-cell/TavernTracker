@echo off
rem Double-click to build TavernTracker.exe into the "dist" folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
pause
