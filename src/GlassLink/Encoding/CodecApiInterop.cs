using System.Runtime.InteropServices;

namespace GlassLink.Encoding;

/// <summary>
/// Minimal ICodecAPI caller. Vortice does not expose ICodecAPI, so the few
/// properties needed for a low-latency CBR encoder are set via its vtable.
/// </summary>
internal static class CodecApiInterop
{
    private static readonly Guid IidICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid AvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AvEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid AvEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773dcb482f8");
    public static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AvEncMpvDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AvEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c48ab7dea22");
    public static readonly Guid AvEncVideoForceKeyFrame = new("398c1b98-8353-4e85-958e-3f3a8cc0b1b8");

    private const ushort VtUi4 = 19;
    private const ushort VtBool = 11;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public short Bool;
        [FieldOffset(8)] public long Int64;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValueDelegate(IntPtr self, ref Guid api, ref PropVariant value);

    public static int SetUInt32(IntPtr comObject, Guid api, uint value) =>
        SetValue(comObject, api, new PropVariant { Vt = VtUi4, UInt32 = value });

    public static int SetBool(IntPtr comObject, Guid api, bool value) =>
        SetValue(comObject, api, new PropVariant { Vt = VtBool, Bool = value ? (short)-1 : (short)0 });

    public static int SetInt64(IntPtr comObject, Guid api, long value) =>
        SetValue(comObject, api, new PropVariant { Vt = 20 /* VT_I8 */, Int64 = value });

    private static int SetValue(IntPtr comObject, Guid api, PropVariant value)
    {
        var iid = IidICodecApi;
        var hr = Marshal.QueryInterface(comObject, in iid, out var codecApi);
        if (hr < 0) return hr;

        try
        {
            var vtable = Marshal.ReadIntPtr(codecApi);
            var method = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
            var setValue = Marshal.GetDelegateForFunctionPointer<SetValueDelegate>(method);
            return setValue(codecApi, ref api, ref value);
        }
        finally
        {
            Marshal.Release(codecApi);
        }
    }
}
