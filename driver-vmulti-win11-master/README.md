# VMulti Windows 11 HID Driver

A Windows 11 compatible update of the Virtual Multiple HID Driver, supporting multitouch, mouse, digitizer, keyboard, and joystick interfaces. This project provides a modernized version of the original [djpnewton/vmulti](https://github.com/djpnewton/vmulti) driver with Windows 11 compatibility and updated build system.

## Features

- **Multi-Platform Support**: x64 and ARM64 architectures
- **Multiple HID Interfaces**: 
  - Keyboard input simulation
  - Mouse input simulation  
  - Multitouch interface
  - Digitizer interface
  - Joystick interface (untested)
- **Windows 11 Compatibility**: Updated from Windows 7 to Windows 11
- **Test Mode Installation**: Self-signed certificate support for development

## System Requirements

- **Windows 11** (x64 or ARM64)
- **Test Mode Enabled** (`bcdedit /set testsigning on`)
- **Visual Studio 2019/2022** with Windows Driver Kit (WDK)
- **Administrator privileges** for installation

## Tested Functionality

✅ **Working**:
- Keyboard input simulation
- Mouse input simulation

❓ **Untested**:
- Gamepad/joystick functionality
- Multitouch interface
- Digitizer interface

## Installation

### Prerequisites

1. **Enable Test Mode** (required for self-signed certificate):
   ```cmd
   bcdedit /set testsigning on
   ```
   Restart your computer after enabling test mode.

2. **Install Certificate**:
   - Navigate to `driver_dist\cert\`
   - Right-click `WDKTestCert.cer` and select "Install Certificate"
   - Choose "Local Machine" → "Trusted Root Certification Authorities"

### Driver Installation Options

#### Option 1: Automated Installer (Recommended)
```cmd
python driver_installer.py
```
The installer provides both GUI and command-line interfaces with automatic architecture detection.

#### Option 2: Manual Installation
1. Open Device Manager
2. Right-click on any device → "Add legacy hardware"
3. Select "Install hardware manually" → "Have Disk"
4. Browse to `driver_dist\x64\` (or `ARM64\`) and select `vmulti.inf`

## Building from Source

### Prerequisites
- Visual Studio 2019/2022 with C++ development tools
- Windows Driver Kit (WDK) for Windows 11
- Administrator privileges

### Build Process
```cmd
# Run as Administrator
build.bat
```

The build script will:
1. Clean previous build artifacts
2. Build client libraries (x64 and ARM64)
3. Build and sign drivers (x64 and ARM64)  
4. Build test applications
5. Package everything into `driver_dist\` directory

### Build Output Structure
```
driver_dist/
├── x64/               # x64 architecture files
│   ├── vmulti.inf     # Driver installation file
│   ├── vmulti.sys     # Signed driver binary
│   ├── vmulticlient.lib
│   └── testvmulti.exe # Test application
├── ARM64/             # ARM64 architecture files
│   ├── vmulti.inf
│   ├── vmulti.sys
│   ├── vmulticlient.lib
│   └── testvmulti.exe
├── cert/              # Certificate files
│   └── WDKTestCert.cer
└── inc/               # Header files
    ├── vmulticlient.h
    └── vmulticommon.h
```

## Development

### Client Library Usage
Include the header files from `driver_dist\inc\` and link against the appropriate `vmulticlient.lib`:

```c
#include "vmulticlient.h"
#include "vmulticommon.h"

// Example usage - see testvmulti.exe source for complete examples
```

### Testing
Use the included test application:
```cmd
cd driver_dist\x64
testvmulti.exe
```

## Certificate Information

This driver uses **self-signed certificates** for development and testing:

- **Certificate**: WDKTestCert (Windows Driver Kit Test Certificate)
- **Purpose**: Development and testing only
- **Requirement**: Windows Test Mode must be enabled
- **Security**: Not suitable for production deployment

⚠️ **Important**: This is a development driver using test certificates. For production use, you would need a properly signed certificate from a trusted Certificate Authority.

## Architecture Support

| Architecture | Build Status | Test Status |
|--------------|-------------|-------------|
| x64          | ✅ Working   | ✅ Tested   |
| ARM64        | ✅ Working   | ✅ Tested   |

## Troubleshooting

### Driver Installation Issues
1. Ensure Test Mode is enabled and system has been restarted
2. Verify certificate is installed in Trusted Root Certification Authorities
3. Run installer as Administrator
4. Check Device Manager for driver conflicts

### Build Issues
1. Ensure Visual Studio and WDK are properly installed
2. Run build script as Administrator
3. Verify Windows SDK version compatibility

## Project Structure

- `driver/kmdf/` - Core driver source code
- `client/` - Client library source
- `test/` - Test application source
- `inc/` - Public header files
- `driver_installer.py` - Python-based installer
- `build.bat` - Complete build script
- `driver_dist/` - Distribution output (created by build)

## License

This project maintains compatibility with the original vmulti project licensing.

## Contributing

This is an updated version of the original vmulti driver. When contributing:
1. Maintain compatibility with both x64 and ARM64 architectures
2. Test on Windows 11 systems
3. Follow the existing code style and structure
4. Update documentation for any new features

## Changelog

### Windows 11 Update
- Updated from Windows 7 to Windows 11 compatibility
- Added ARM64 architecture support
- Modernized build system with automated script
- Added comprehensive installer with GUI/CLI options
- Updated certificate handling for test mode
- Comprehensive testing on Windows 11 x64/ARM64

## Original Project

This is an updated version of the original [djpnewton/vmulti](https://github.com/djpnewton/vmulti) Virtual Multiple HID Driver.