using System.Runtime.InteropServices;

namespace GlassLink.Input.Native;

internal static class SendInputNative
{
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;

    public const uint KeyEventfExtendedKey = 0x0001;
    public const uint KeyEventfKeyup = 0x0002;
    public const uint KeyEventfUnicode = 0x0004;
    public const uint KeyEventfScancode = 0x0008;

    public const uint MouseEventfMove = 0x0001;
    public const uint MouseEventfLeftDown = 0x0002;
    public const uint MouseEventfLeftUp = 0x0004;
    public const uint MouseEventfRightDown = 0x0008;
    public const uint MouseEventfRightUp = 0x0010;
    public const uint MouseEventfMiddleDown = 0x0020;
    public const uint MouseEventfMiddleUp = 0x0040;
    public const uint MouseEventfXDown = 0x0080;
    public const uint MouseEventfXUp = 0x0100;
    public const uint MouseEventfWheel = 0x0800;
    public const uint MouseEventfHWheel = 0x1000;
    public const uint MouseEventfVirtualDesk = 0x4000;
    public const uint MouseEventfAbsolute = 0x8000;

    public const uint XButton1 = 0x0001;
    public const uint XButton2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    public static uint Send(ref Input input) => SendInput(1, new[] { input }, Marshal.SizeOf<Input>());

    public static Input CreateMouse(int dx, int dy, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = data,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    public static Input CreateKeyboard(ushort scanOrChar, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = 0,
                ScanCode = scanOrChar,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };
}
