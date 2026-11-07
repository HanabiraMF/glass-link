@echo off
setlocal
title GlassLink Input Driver Installer
echo This window must be started with "Run as administrator".
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-input-drivers.ps1" %*
echo.
pause
endlocal
