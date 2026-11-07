using System.Runtime.InteropServices;

namespace GlassLink.Input.Native;

/// <summary>
/// Windows 8+ Touch Injection API. Structures mirror the Windows SDK POINTER_INFO /
/// POINTER_TOUCH_INFO layout for x64. The API is user-mode and requires no driver.
/// </summary>
internal static class NativeTouchInjection
{
    public const uint PtTouch = 2;

    public const uint PointerFlagNew = 0x00000001;
    public const uint PointerFlagInRange = 0x00000002;
    public const uint PointerFlagInContact = 0x00000004;
    public const uint PointerFlagDown = 0x00010000;
    public const uint PointerFlagUpdate = 0x00020000;
    public const uint PointerFlagUp = 0x00040000;

    public const uint TouchMaskNone = 0x00000000;
    public const uint TouchMaskContactArea = 0x00000001;
    public const uint TouchMaskOrientation = 0x00000002;
    public const uint TouchMaskPressure = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public NativePoint PixelLocation;
        public NativePoint HimetricLocation;
        public NativePoint PixelLocationRaw;
        public NativePoint HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public NativeRect Contact;
        public NativeRect ContactRaw;
        public uint Orientation;
        public uint Pressure;

        // Convenience properties over the nested structures.
        public uint PointerType { get => PointerInfo.PointerType; set => PointerInfo.PointerType = value; }
        public uint PointerId { get => PointerInfo.PointerId; set => PointerInfo.PointerId = value; }
        public uint PointerFlags { get => PointerInfo.PointerFlags; set => PointerInfo.PointerFlags = value; }
        public int PixelX { get => PointerInfo.PixelLocation.X; set => PointerInfo.PixelLocation.X = value; }
        public int PixelY { get => PointerInfo.PixelLocation.Y; set => PointerInfo.PixelLocation.Y = value; }
        public int HimetricX { get => PointerInfo.HimetricLocation.X; set => PointerInfo.HimetricLocation.X = value; }
        public int HimetricY { get => PointerInfo.HimetricLocation.Y; set => PointerInfo.HimetricLocation.Y = value; }
        public int PixelXRaw { get => PointerInfo.PixelLocationRaw.X; set => PointerInfo.PixelLocationRaw.X = value; }
        public int PixelYRaw { get => PointerInfo.PixelLocationRaw.Y; set => PointerInfo.PixelLocationRaw.Y = value; }
        public int HimetricXRaw { get => PointerInfo.HimetricLocationRaw.X; set => PointerInfo.HimetricLocationRaw.X = value; }
        public int HimetricYRaw { get => PointerInfo.HimetricLocationRaw.Y; set => PointerInfo.HimetricLocationRaw.Y = value; }
        public int ContactLeft { get => Contact.Left; set => Contact.Left = value; }
        public int ContactTop { get => Contact.Top; set => Contact.Top = value; }
        public int ContactRight { get => Contact.Right; set => Contact.Right = value; }
        public int ContactBottom { get => Contact.Bottom; set => Contact.Bottom = value; }
        public int ContactRawLeft { get => ContactRaw.Left; set => ContactRaw.Left = value; }
        public int ContactRawTop { get => ContactRaw.Top; set => ContactRaw.Top = value; }
        public int ContactRawRight { get => ContactRaw.Right; set => ContactRaw.Right = value; }
        public int ContactRawBottom { get => ContactRaw.Bottom; set => ContactRaw.Bottom = value; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);
}
