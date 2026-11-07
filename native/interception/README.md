# Interception runtime

Place the x64 `interception.dll` from the official Interception release here:

```text
native\interception\interception.dll
```

`scripts/install-interception.ps1` downloads the release, installs the kernel
filter driver and copies this DLL automatically.

The driver must be installed and Windows rebooted before GlassLink can use the
virtual hardware layer. Without it, GlassLink reports the missing driver and
falls back to the software injection layer.

- Upstream: https://github.com/oblitum/Interception
- License: LGPL 3.0 for non-commercial usage; commercial use requires a license.
- Do not commit third-party binaries into a public repository unless their
  license allows redistribution.
