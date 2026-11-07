@echo off
setlocal
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
echo Removing old HIDMaestro virtual devices and driver...
"Çý¶¯ÇåÀí\HIDMaestroUninstall.exe"
echo Done. Start GlassLink again; it will reinstall HIDMaestro and create a generic mouse.
pause
