using System.Reflection;
using System.Runtime.InteropServices;
using GlassLink.Input.Hardware;

namespace GlassLink.Input;

public static class NativeLibraryResolver
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("interception", StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("vmulticlient", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var fileName = libraryName + ".dll";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "native", fileName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", fileName),
            Path.Combine(Environment.CurrentDirectory, "native", fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
