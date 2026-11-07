using System.Runtime.InteropServices;

namespace GlassLink.Input.Hardware;

/// <summary>
/// P/Invoke surface for vmulti-win11's vmulticlient DLL.
///
/// Upstream ships client.c as a static library, so this repository contains a
/// small native shim (native/vmulticlient-shim) that compiles client.c into a
/// DLL exporting the exact C API declared in vmulticlient.h.
/// </summary>
internal static class VMultiNative
{
    public const string Dll = "vmulticlient";

    public const byte ReportIdMtouch = 0x01;
    public const byte MultiTipSwitchBit = 1;
    public const byte MultiInRangeBit = 2;
    public const byte MultiConfidenceBit = 4;
    public const ushort MultiMaxCoordinate = 0x7FFF;
    public const int MultiMaxCount = 20;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Touch
    {
        public byte Status;
        public byte ContactId;
        public ushort XValue;
        public ushort YValue;
        public ushort Width;
        public ushort Height;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr vmulti_alloc();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vmulti_free(IntPtr vmulti);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool vmulti_connect(IntPtr vmulti);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vmulti_disconnect(IntPtr vmulti);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool vmulti_update_multitouch(IntPtr vmulti, [In] Touch[] touches, byte actualCount);
}
