# Third-party notices

GlassLink uses the following third-party components. Their licenses apply to the
corresponding files and binaries.

## Vortice.Windows

- Packages: `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.MediaFoundation`
- Author: Amer Koleci and contributors
- License: MIT
- Source: https://github.com/amerkoleci/Vortice.Windows

## vmulti-win11 client source

- Files: `native/vmulticlient-shim/vendor/*`
- Source: https://github.com/xaviersykora/vmulti-win11
- The vendored files are the upstream vmulti client API/source, used to build
  `vmulticlient.dll`. See the upstream repository for the exact license text.
- The vmulti kernel driver is not distributed by GlassLink. Build/install it
  separately from upstream.

## Interception

- Runtime/API: https://github.com/oblitum/Interception
- The Interception driver and library are **not** bundled in this repository.
  Install them via `scripts/install-interception.ps1` or manually.
- License: LGPL 3.0 for non-commercial use. Commercial usage requires a
  commercial license from the author. See the Interception repository licenses.

## Microsoft .NET / ASP.NET Core

- Runtime and framework references are subject to the .NET Foundation licenses.

## Fonts / icons

- The included SVG logo is original to this project.
- No external web fonts or icon libraries are required. UI uses system fonts.
