@echo off
setlocal
cd /d "%~dp0"
echo Starting GlassLink development build...
dotnet run --project "1-源代码\src\GlassLink\GlassLink.csproj" -c Release -- --controller=auto
endlocal
