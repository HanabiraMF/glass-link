using System.Runtime.InteropServices;
using GlassLink.Protocol;

namespace GlassLink.Capture;

public readonly record struct CursorSnapshot(bool Visible, double X, double Y);

/// <summary>
/// Reads the host cursor position so the browser can draw the controlled
/// machine's pointer as an overlay. DXGI Desktop Duplication exposes the cursor
/// separately from the desktop image; this keeps the host pointer visible while
/// the local browser pointer is hidden.
/// </summary>
public static class CursorTracker
{
    private const int CursorShowing = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public NativePoint ScreenPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    public static CursorSnapshot Get(CaptureBounds bounds)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
            return new CursorSnapshot(false, 0, 0);
        if ((info.Flags & CursorShowing) == 0)
            return new CursorSnapshot(false, 0, 0);

        var x = info.ScreenPosition.X;
        var y = info.ScreenPosition.Y;
        if (x < bounds.X || y < bounds.Y || x >= bounds.X + bounds.Width || y >= bounds.Y + bounds.Height)
            return new CursorSnapshot(false, 0, 0);

        var nx = (x - bounds.X) / (double)Math.Max(1, bounds.Width - 1);
        var ny = (y - bounds.Y) / (double)Math.Max(1, bounds.Height - 1);
        return new CursorSnapshot(true, Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
    }
}
