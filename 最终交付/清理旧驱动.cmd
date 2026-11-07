@echo off
setlocal
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
set "VMDIR=driver-VMulti.Driver"
if not exist "%VMDIR%\devcon.exe" set "VMDIR=Çý¶¯ÇåÀí\driver-VMulti.Driver"
echo Removing old Pentablet / vmulti virtual devices...
if exist "%VMDIR%\devcon.exe" "%VMDIR%\devcon.exe" remove "pentablet\hid"
if exist "%VMDIR%\DIFxCmd.exe" "%VMDIR%\DIFxCmd.exe" /u "%VMDIR%\vmulti.inf"
sc stop vmulti >nul 2>&1
sc delete vmulti >nul 2>&1
echo Done. HIDMaestro devices are kept.
pause
