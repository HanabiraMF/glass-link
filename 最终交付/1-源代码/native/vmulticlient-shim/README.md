# vmulticlient.dll shim

GlassLink needs a **DLL** named `vmulticlient.dll` exposing the C API from the
upstream vmulti client. The vmulti-win11 project ships that client code as a
static `vmulticlient.lib`, so this folder vendors the upstream client source and
builds it into an x64 DLL with the required exports.

## Build

```powershell
pwsh -File native/vmulticlient-shim/build.ps1
```

Output: `native/vmulticlient-shim/bin/vmulticlient.dll`.

Then build GlassLink; the csproj copies the DLL to `native/vmulticlient.dll` in
the app output directory. `NativeLibraryResolver` searches both the app root and
`native\`.

## Exports

The `.def` file exports the complete upstream client API, including:

- `vmulti_alloc` / `vmulti_free`
- `vmulti_connect` / `vmulti_disconnect`
- `vmulti_update_multitouch` (used by GlassLink for 20-contact virtual touch)
- `vmulti_update_keyboard`, `vmulti_update_relative_mouse`, etc.

GlassLink's managed P/Invoke definitions are in
`src/GlassLink/Input/Hardware/VMultiNative.cs`.

## Source provenance

`vendor/client.c`, `vendor/vmulticlient.h`, `vendor/vmulticommon.h` are copied
from [xaviersykora/vmulti-win11](https://github.com/xaviersykora/vmulti-win11)
(MIT-style upstream project; see the upstream README and repository license).
The client library has no driver code; the vmulti.sys driver must be installed
separately.

## Driver

The DLL alone is not enough. Install the vmulti-win11 virtual HID driver first:

1. Enable test signing.
2. Trust the driver test certificate.
3. Install `vmulti.inf` as an administrator.
4. Reboot and run `testvmulti.exe /multitouch`.

See [docs/DRIVERS.md](../../docs/DRIVERS.md) for the complete procedure.
