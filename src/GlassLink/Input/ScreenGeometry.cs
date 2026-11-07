using System.Runtime.InteropServices;
using GlassLink.Protocol;

namespace GlassLink.Input;

public static partial class ScreenGeometry
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmMaximumTouches = 95;

    public static int MaximumTouchContacts => Math.Max(0, GetSystemMetrics(SmMaximumTouches));

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    public static CaptureBounds PrimaryBounds()
    {
        var width = Math.Max(1, GetSystemMetrics(SmCxScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyScreen));
        return new CaptureBounds(0, 0, width, height);
    }

    public static CaptureBounds VirtualBounds()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return new CaptureBounds(x, y, width, height);
    }
}
