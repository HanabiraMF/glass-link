using System.Runtime.InteropServices;

namespace GlassLink.Input.Hardware;

/// <summary>
/// P/Invoke surface for the Interception kernel HID filter driver.
/// Source/API documentation: https://github.com/oblitum/Interception
/// The native interception.dll must be next to the executable or in native\.
/// </summary>
internal static class InterceptionNative
{
    public const string Dll = "interception";

    public const int MaxKeyboard = 10;
    public const int MaxMouse = 10;
    public const int MaxDevice = MaxKeyboard + MaxMouse;

    public const ushort KeyDown = 0x00;
    public const ushort KeyUp = 0x01;
    public const ushort KeyE0 = 0x02;
    public const ushort KeyE1 = 0x04;

    public const ushort MouseLeftDown = 0x001;
    public const ushort MouseLeftUp = 0x002;
    public const ushort MouseRightDown = 0x004;
    public const ushort MouseRightUp = 0x008;
    public const ushort MouseMiddleDown = 0x010;
    public const ushort MouseMiddleUp = 0x020;
    public const ushort MouseButton4Down = 0x040;
    public const ushort MouseButton4Up = 0x080;
    public const ushort MouseButton5Down = 0x100;
    public const ushort MouseButton5Up = 0x200;
    public const ushort MouseWheel = 0x400;
    public const ushort MouseHWheel = 0x800;

    public const ushort MouseMoveRelative = 0x000;
    public const ushort MouseMoveAbsolute = 0x001;
    public const ushort MouseVirtualDesktop = 0x002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyStroke
    {
        public ushort Code;
        public ushort State;
        public uint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseStroke
    {
        public ushort State;
        public ushort Flags;
        public short Rolling;
        public int X;
        public int Y;
        public uint Information;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InterceptionPredicate(int device);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr interception_create_context();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void interception_destroy_context(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void interception_set_filter(IntPtr context, InterceptionPredicate predicate, ushort filter);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_send(IntPtr context, int device, ref KeyStroke stroke, uint nstroke);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_send(IntPtr context, int device, ref MouseStroke stroke, uint nstroke);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern uint interception_get_hardware_id(IntPtr context, int device, [Out] char[] hardwareIdBuffer, uint bufferSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_invalid(int device);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_keyboard(int device);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_mouse(int device);

    public static int KeyboardDevice(int index) => index + 1;
    public static int MouseDevice(int index) => MaxKeyboard + index + 1;
}
