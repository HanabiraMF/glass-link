using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GlassLink.Input.Hardware;

/// <summary>
/// Owns an Interception context and performs the required device-ID discovery /
/// warm-up. Interception multiplexes up to 10 keyboard and 10 mouse device slots;
/// before injecting we enumerate the slots, query their hardware IDs, and send a
/// zero-delta warm-up mouse stroke to find a slot accepted by the driver.
/// </summary>
internal sealed class InterceptionSession : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<int, string> _hardwareIds = new();
    private IntPtr _context;
    private long _lastErrorLogTicks;

    private InterceptionSession(IntPtr context, int keyboardDevice, int mouseDevice, IReadOnlyList<string> diagnostics, ILogger logger)
    {
        _context = context;
        KeyboardDevice = keyboardDevice;
        MouseDevice = mouseDevice;
        Diagnostics = diagnostics;
        _logger = logger;
    }

    public int KeyboardDevice { get; private set; }
    public int MouseDevice { get; private set; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsValid => _context != IntPtr.Zero;

    public static InterceptionSession? TryCreate(ILogger logger, out string? error, out List<string> diagnostics)
    {
        error = null;
        diagnostics = new List<string>();
        IntPtr context = IntPtr.Zero;

        try
        {
            context = InterceptionNative.interception_create_context();
            if (context == IntPtr.Zero)
            {
                var win32 = new Win32Exception(Marshal.GetLastWin32Error());
                error = $"Interception 内核驱动未安装或不可用（interception_create_context 失败：{win32.Message}）。";
                diagnostics.Add("请先安装 Interception 驱动并重启系统。");
                diagnostics.Add("驱动安装脚本：scripts/install-interception.ps1（需管理员权限）。");
                return null;
            }

            var ids = new Dictionary<int, string>();
            var keyboardCandidates = new List<int>();
            var mouseCandidates = new List<int>();

            for (var i = 0; i < InterceptionNative.MaxKeyboard; i++)
            {
                var device = InterceptionNative.KeyboardDevice(i);
                if (InterceptionNative.interception_is_invalid(device) != 0) continue;
                if (InterceptionNative.interception_is_keyboard(device) != 0)
                {
                    keyboardCandidates.Add(device);
                    var hwid = ReadHardwareId(context, device);
                    if (!string.IsNullOrWhiteSpace(hwid)) ids[device] = hwid;
                }
            }

            for (var i = 0; i < InterceptionNative.MaxMouse; i++)
            {
                var device = InterceptionNative.MouseDevice(i);
                if (InterceptionNative.interception_is_invalid(device) != 0) continue;
                if (InterceptionNative.interception_is_mouse(device) != 0)
                {
                    mouseCandidates.Add(device);
                    var hwid = ReadHardwareId(context, device);
                    if (!string.IsNullOrWhiteSpace(hwid)) ids[device] = hwid;
                }
            }

            var keyboardDevice = keyboardCandidates.FirstOrDefault(device => ids.ContainsKey(device), keyboardCandidates.FirstOrDefault(InterceptionNative.KeyboardDevice(0)));
            var mouseDevice = 0;

            // "Warm-up": initialize the driver's injection path with an inert
            // zero-delta relative mouse report. The first slot that accepts the
            // report is used for real mouse injection. This also verifies that we
            // are not merely holding a valid but non-injectable device slot.
            foreach (var candidate in mouseCandidates.DefaultIfEmpty(InterceptionNative.MouseDevice(0)))
            {
                var stroke = new InterceptionNative.MouseStroke
                {
                    State = 0,
                    Flags = InterceptionNative.MouseMoveRelative,
                    Rolling = 0,
                    X = 0,
                    Y = 0,
                    Information = 0
                };
                if (InterceptionNative.interception_send(context, candidate, ref stroke, 1) > 0)
                {
                    mouseDevice = candidate;
                    break;
                }
            }

            if (mouseDevice == 0)
            {
                mouseDevice = mouseCandidates.FirstOrDefault(InterceptionNative.MouseDevice(0));
                diagnostics.Add("Interception 鼠标热身报告未被驱动接受；请检查驱动安装后重启。");
            }

            diagnostics.Add($"Interception 设备 ID 学习完成：键盘={keyboardDevice}，鼠标={mouseDevice}，枚举到 {ids.Count} 个硬件 ID。");
            diagnostics.Add("Interception 使用内核 HID 过滤器注入扫描码，可穿过 UAC / Secure Desktop 并被全屏 DirectX/Unity/Unreal 接受。");

            logger.LogInformation("Interception warm-up complete. KeyboardDevice={Keyboard}, MouseDevice={Mouse}, HardwareIds={Count}.",
                keyboardDevice, mouseDevice, ids.Count);

            var session = new InterceptionSession(context, keyboardDevice, mouseDevice, diagnostics, logger);
            foreach (var pair in ids) session._hardwareIds[pair.Key] = pair.Value;
            return session;
        }
        catch (DllNotFoundException)
        {
            error = "找不到 interception.dll。请把官方 Interception 运行库放到程序目录或 native\\ 目录。";
            diagnostics.Add("Interception 驱动和 interception.dll 均需安装，缺一不可。");
            if (context != IntPtr.Zero) InterceptionNative.interception_destroy_context(context);
            return null;
        }
        catch (BadImageFormatException)
        {
            error = "interception.dll 架构不匹配。本项目为 x64，请使用 x64 版本。";
            if (context != IntPtr.Zero) InterceptionNative.interception_destroy_context(context);
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"interception.dll 版本不兼容：{ex.Message}";
            if (context != IntPtr.Zero) InterceptionNative.interception_destroy_context(context);
            return null;
        }
        catch (Exception ex)
        {
            error = $"Interception 初始化异常：{ex.Message}";
            diagnostics.Add(ex.GetType().Name);
            if (context != IntPtr.Zero) InterceptionNative.interception_destroy_context(context);
            return null;
        }
    }

    private static string ReadHardwareId(IntPtr context, int device)
    {
        try
        {
            var buffer = new char[512];
            var length = InterceptionNative.interception_get_hardware_id(context, device, buffer, (uint)(buffer.Length * sizeof(char)));
            if (length == 0) return "";
            var count = (int)Math.Min(length, (uint)buffer.Length);
            while (count > 0 && buffer[count - 1] == '\0') count--;
            return new string(buffer, 0, count);
        }
        catch
        {
            return "";
        }
    }

    public bool SendKey(ushort scanCode, bool down, bool extended)
    {
        if (_context == IntPtr.Zero) return false;
        var state = down ? InterceptionNative.KeyDown : InterceptionNative.KeyUp;
        if (extended) state |= InterceptionNative.KeyE0;

        var stroke = new InterceptionNative.KeyStroke
        {
            Code = scanCode,
            State = state,
            Information = 0
        };
        try
        {
            var sent = InterceptionNative.interception_send(_context, KeyboardDevice, ref stroke, 1);
            return sent > 0;
        }
        catch (Exception ex)
        {
            LogRateLimited(ex, "Interception keyboard send failed");
            return false;
        }
    }

    public bool SendMouse(InterceptionNative.MouseStroke stroke)
    {
        if (_context == IntPtr.Zero) return false;
        try
        {
            return InterceptionNative.interception_send(_context, MouseDevice, ref stroke, 1) > 0;
        }
        catch (Exception ex)
        {
            LogRateLimited(ex, "Interception mouse send failed");
            return false;
        }
    }

    private void LogRateLimited(Exception ex, string message)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastErrorLogTicks);
        if (now - previous < 2000) return;
        Interlocked.Exchange(ref _lastErrorLogTicks, now);
        _logger.LogWarning(ex, "{Message}", message);
    }

    public void Dispose()
    {
        if (_context == IntPtr.Zero) return;
        var context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        try
        {
            InterceptionNative.interception_destroy_context(context);
        }
        catch
        {
            // Native teardown failures must not take down the server.
        }
    }
}
