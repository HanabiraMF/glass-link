# GlassLink input driver bundle

This folder is installed as `<install>\drivers`.

- `install-input-drivers.cmd` self-elevates and runs the PowerShell installer.
- The installer detects existing drivers, downloads Interception if necessary, and installs vmulti-win11 from an optional payload.
- GlassLink never crashes if these drivers are absent; it falls back to software input.

## Optional payload layout

To make the installer fully offline, place files here before building the installer:

```text
drivers/payload/Interception/
  install-interception.exe
  library/x64/interception.dll   (the PowerShell script searches recursively)

drivers/payload/vmulti-win11/
  driver_dist/x64/vmulti.inf
  driver_dist/x64/vmulti.sys
  driver_dist/x64/testvmulti.exe
  driver_dist/x64/vmulticlient.dll   (optional; GlassLink's native shim DLL is used otherwise)
  driver_dist/cert/WDKTestCert.cer
```

`vmulti-win11` has no official prebuilt Windows 11 release; build it from
<https://github.com/xaviersykora/vmulti-win11> with Visual Studio + WDK. The
installer intentionally does not download or execute unsigned kernel drivers
from unofficial sources.

After installation, reboot and verify with `testvmulti.exe /multitouch`.
See the main [driver documentation](../../docs/DRIVERS.md).
