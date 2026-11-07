@echo off
setlocal enabledelayedexpansion

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo This script requires Administrator privileges.
    echo Please run this script as Administrator.
    exit /b 1
)

:: Clean previous build artifacts
echo Cleaning previous build artifacts...

:: Clean distribution directory
if exist driver_dist rmdir /s /q driver_dist

:: Clean driver output directories
if exist driver\kmdf\x64\Release rmdir /s /q driver\kmdf\x64\Release
if exist driver\kmdf\ARM64\Release rmdir /s /q driver\kmdf\ARM64\Release

:: Clean client output directories
if exist client\x64\Release rmdir /s /q client\x64\Release
if exist client\ARM64\Release rmdir /s /q client\ARM64\Release

:: Clean test output directories
if exist test\x64\Release rmdir /s /q test\x64\Release
if exist test\ARM64\Release rmdir /s /q test\ARM64\Release

:: Clean solution-level output directories
if exist x64\Release rmdir /s /q x64\Release
if exist ARM64\Release rmdir /s /q ARM64\Release

echo Cleaning completed.

:: Set up Visual Studio environment if needed
if not defined VSINSTALLDIR (
    echo Setting up Visual Studio environment...
    call :FindVS
    if !ERRORLEVEL! neq 0 exit /b !ERRORLEVEL!
)

:: Create the distribution directory
set DIST_DIR=driver_dist
:: Already removed in clean step
mkdir %DIST_DIR%
mkdir %DIST_DIR%\x64
mkdir %DIST_DIR%\ARM64
mkdir %DIST_DIR%\inc

echo Building VMulti Windows 11 driver...

:: The WDK test certificate is automatically managed by the build process
echo Using WDK test certificate for driver signing...

:: Build the client library first (since it's a dependency)
echo Building vmulticlient library...
msbuild client\vmulticlient.vcxproj /p:Configuration=Release /p:Platform=x64 /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build x64 vmulticlient library.
    exit /b 1
)
msbuild client\vmulticlient.vcxproj /p:Configuration=Release /p:Platform=ARM64 /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build ARM64 vmulticlient library.
    exit /b 1
)

:: Build the driver
echo Building x64 driver...
msbuild driver\kmdf\vmulti.vcxproj /p:Configuration=Release /p:Platform=x64 /p:TestSign=false /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build x64 driver.
    exit /b 1
)

:: Driver is already signed with WDKTestCert during the build process
echo x64 driver is signed with WDKTestCert for test mode.

echo Building ARM64 driver...
msbuild driver\kmdf\vmulti.vcxproj /p:Configuration=Release /p:Platform=ARM64 /p:TestSign=false /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build ARM64 driver.
    exit /b 1
)

:: Driver is already signed with WDKTestCert during the build process
echo ARM64 driver is signed with WDKTestCert for test mode.

:: Build the test application
echo Building x64 test application...
msbuild test\testvmulti.vcxproj /p:Configuration=Release /p:Platform=x64 /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build x64 test application.
    exit /b 1
)

echo Building ARM64 test application...
msbuild test\testvmulti.vcxproj /p:Configuration=Release /p:Platform=ARM64 /m
if !ERRORLEVEL! neq 0 (
    echo Failed to build ARM64 test application.
    exit /b 1
)

:: Copy x64 files to distribution directory
echo Copying x64 files to distribution directory...

:: Copy the driver package files
xcopy /Y driver\kmdf\x64\Release\vmulti\*.* %DIST_DIR%\x64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy x64 driver package files

:: Copy the raw driver files too for reference
copy driver\kmdf\x64\Release\vmulti.sys %DIST_DIR%\x64\vmulti_raw.sys 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy raw x64 driver file

:: Copy client library
copy client\x64\Release\vmulticlient.lib %DIST_DIR%\x64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy client\x64\Release\vmulticlient.lib

:: Copy test application
echo Copying test application...
copy test\x64\Release\testvmulti.exe %DIST_DIR%\x64\ 2>nul
if !ERRORLEVEL! neq 0 (
    echo Warning: Failed to copy test\x64\Release\testvmulti.exe
) else (
    echo Successfully copied test executable to %DIST_DIR%\x64\testvmulti.exe
)

copy x64\Release\testvmulti.exe %DIST_DIR%\x64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy x64\Release\testvmulti.exe

:: Copy ARM64 files to distribution directory
echo Copying ARM64 files to distribution directory...

:: Copy the driver package files
xcopy /Y driver\kmdf\ARM64\Release\vmulti\*.* %DIST_DIR%\ARM64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy ARM64 driver package files

:: Copy the raw driver files too for reference
copy driver\kmdf\ARM64\Release\vmulti.sys %DIST_DIR%\ARM64\vmulti_raw.sys 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy raw ARM64 driver file

:: Copy client library
copy client\ARM64\Release\vmulticlient.lib %DIST_DIR%\ARM64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy client\ARM64\Release\vmulticlient.lib

:: Copy test application
echo Copying ARM64 test application...
copy test\ARM64\Release\testvmulti.exe %DIST_DIR%\ARM64\ 2>nul
if !ERRORLEVEL! neq 0 (
    echo Warning: Failed to copy test\ARM64\Release\testvmulti.exe
) else (
    echo Successfully copied ARM64 test executable to %DIST_DIR%\ARM64\testvmulti.exe
)

copy ARM64\Release\testvmulti.exe %DIST_DIR%\ARM64\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy ARM64\Release\testvmulti.exe

:: Copy header files
echo Copying header files...
copy inc\vmulticlient.h %DIST_DIR%\inc\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy inc\vmulticlient.h

copy inc\vmulticommon.h %DIST_DIR%\inc\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy inc\vmulticommon.h

:: Copy documentation
echo Copying documentation...
copy README.md %DIST_DIR%\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy README.md

copy status.md %DIST_DIR%\ 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy status.md

:: Copy certificate files
echo Copying certificate files...
mkdir %DIST_DIR%\cert 2>nul

:: Copy the WDKTestCert certificate used for signing
copy driver\kmdf\x64\Release\vmulti.cer %DIST_DIR%\cert\WDKTestCert.cer 2>nul
if !ERRORLEVEL! neq 0 echo Warning: Failed to copy WDKTestCert certificate

echo.
echo Build completed successfully!
echo Driver files are available in the %DIST_DIR% directory.
echo.
echo To install the driver in test mode:
echo 1. Enable test mode: bcdedit /set testsigning on
echo 2. Install the WDKTestCert certificate from %DIST_DIR%\cert\WDKTestCert.cer
echo 3. Use Device Manager to install the driver with %DIST_DIR%\x64\vmulti.inf

goto :EOF

:FindVS
:: Try to find Visual Studio installation
for %%v in (2022 2019) do (
    for %%e in (Enterprise Professional Community) do (
        set "VS_PATH=C:\Program Files\Microsoft Visual Studio\%%v\%%e\Common7\Tools\VsDevCmd.bat"
        if exist "!VS_PATH!" (
            echo Found Visual Studio %%v %%e
            call "!VS_PATH!" -arch=amd64 -host_arch=amd64
            goto :EOF
        )
        
        set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\%%v\%%e\Common7\Tools\VsDevCmd.bat"
        if exist "!VS_PATH!" (
            echo Found Visual Studio %%v %%e
            call "!VS_PATH!" -arch=amd64 -host_arch=amd64
            goto :EOF
        )
    )
)

echo Error: Visual Studio not found. Please run this script from a Developer Command Prompt for VS.
exit /b 1