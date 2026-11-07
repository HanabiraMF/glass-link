@echo off
setlocal
cd /d "%~dp0"
if not exist "3-免安装构建产物\GlassLink.exe" (
  echo Missing 3-免安装构建产物\GlassLink.exe
  pause
  exit /b 1
)
start "" "3-免安装构建产物\GlassLink.exe"
endlocal
