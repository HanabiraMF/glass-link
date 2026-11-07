using System.ComponentModel;
using System.Runtime.InteropServices;
using GlassLink.Input.Native;
using GlassLink.Protocol;
using Microsoft.Extensions.Logging;

namespace GlassLink.Input.Software;

/// <summary>
/// Driver-free user-mode injection layer.
///
/// Keyboard/mouse: SendInput with Set-1 scan codes.
/// Touch: Windows Touch Injection (InitializeTouchInjection / InjectTouchInput).
///
/// Known limitations, intentionally surfaced in the UI:
///  - Full-screen DirectX / Unity / Unreal games may discard injected input.
///  - Secure Desktop / UAC prompts are not reachable from a normal-integrity process.
///  - Some anti-cheat and protected surfaces ignore SendInput.
/// This layer is for normal desktop, browser and windowed applications. It is
/// not the recommended path for full-screen games; use the virtual hardware layer.
/// </summary>
public sealed class SoftwareInputController : IInputController
{
    private const int MaxTouchContacts = 20;
    private const uint TouchFeedbackDefault = 0x1;

    private readonly ILogger<SoftwareInputController> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<int, TouchContact> _activeTouches = new();
    private bool _touchInitialized;
    private bool _touchFallbackToMouse;
    private int? _fallbackTouchId;
    private bool _fallbackMouseDown;
    private double _fallbackX;
    private double _fallbackY;
    private bool _operational;
    private long _lastTouchErrorLogTicks;
    private CaptureBounds _bounds;

    public SoftwareInputController(ILogger<SoftwareInputController> logger)
    {
        _logger = logger;
        _bounds = ScreenGeometry.PrimaryBounds();
    }

    public string LayerId => ControllerLayerIds.Software;
    public string DisplayName => "软件注入层（SendInput + Touch Injection）";
    public CaptureBounds Bounds => _bounds;
    public bool IsOperational => _operational;

    public Task<ControllerActivation> ActivateAsync(CaptureBounds bounds, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _bounds = bounds.Width > 0 && bounds.Height > 0 ? bounds : ScreenGeometry.PrimaryBounds();
            _operational = true;
            _touchInitialized = false;
            _touchFallbackToMouse = false;
            _activeTouches.Clear();
            _fallbackTouchId = null;
            _fallbackMouseDown = false;

            // Windows touch injection needs a touch digitizer. On a desktop without
            // one SM_MAXIMUMTOUCHES is 0 and InjectTouchInput fails with error 87.
            // Keep keyboard/mouse working and map a single finger to the mouse.
            try
            {
                if (ScreenGeometry.MaximumTouchContacts <= 0)
                {
                    _touchFallbackToMouse = true;
                    _logger.LogWarning("系统没有触摸数字化仪（SM_MAXIMUMTOUCHES=0），单指触摸将回退为鼠标；多指手势需要 vmulti 虚拟触摸屏。");
                }
                else
                {
                    _touchInitialized = NativeTouchInjection.InitializeTouchInjection(MaxTouchContacts, TouchFeedbackDefault);
                    if (!_touchInitialized)
                    {
                        _touchFallbackToMouse = true;
                        var error = new Win32Exception(Marshal.GetLastWin32Error());
                        _logger.LogWarning("Touch Injection unavailable: {Error}. Falling back to single-finger mouse.", error.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _touchFallbackToMouse = true;
                _logger.LogWarning(ex, "Touch Injection initialization failed; falling back to single-finger mouse.");
            }

            var diagnostics = new List<string>
            {
                "无驱动依赖，启动即可用。",
                "键盘/鼠标使用 SendInput 扫描码注入。",
                _touchInitialized
                    ? "多指触摸使用 Windows Touch Injection（最多 20 触点）。"
                    : _touchFallbackToMouse
                        ? "本机无触摸数字化仪：单指触摸已回退为鼠标；多指需要 vmulti 虚拟触摸屏。"
                        : "Touch Injection 不可用，触摸已回退为单指鼠标。",
                "局限：全屏 DirectX/Unity/Unreal、反作弊、Secure Desktop（UAC）下可能被忽略。全屏游戏请切换虚拟硬件层。"
            };

            _logger.LogInformation("Software input layer activated. Bounds={X},{Y} {W}x{H}; touch={Touch}",
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, _touchInitialized);

            return Task.FromResult(new ControllerActivation(true, LayerId, LayerId, null, diagnostics));
        }
    }

    public void UpdateBounds(CaptureBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        lock (_sync) _bounds = bounds;
    }

    public bool Inject(InputEvent evt)
    {
        if (!_operational) return false;

        try
        {
            return evt.Type switch
            {
                InputTypeNames.Keyboard => InjectKeyboard(evt),
                InputTypeNames.MouseMove => InjectMouseMove(evt),
                InputTypeNames.MouseButton => InjectMouseButton(evt),
                InputTypeNames.MouseWheel => InjectMouseWheel(evt),
                InputTypeNames.Touch => InjectTouch(evt),
                InputTypeNames.Text => InjectText(evt),
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Software injection failed for event {Type}.", evt.Type);
            return false;
        }
    }

    private static bool InjectKeyboard(InputEvent evt)
    {
        if (!KeyMap.TryResolve(evt, out var key) || key.ScanCode <= 0)
            return false;
        if (!string.Equals(evt.Action, "down", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evt.Action, "up", StringComparison.OrdinalIgnoreCase))
            return false;

        var flags = SendInputNative.KeyEventfScancode;
        if (string.Equals(evt.Action, "up", StringComparison.OrdinalIgnoreCase))
            flags |= SendInputNative.KeyEventfKeyup;
        if (key.Extended)
            flags |= SendInputNative.KeyEventfExtendedKey;

        var input = SendInputNative.CreateKeyboard((ushort)key.ScanCode, flags);
        return SendInputNative.Send(ref input) == 1;
    }

    private bool InjectMouseMove(InputEvent evt)
    {
        var mode = evt.Mode ?? "relative";
        if (string.Equals(mode, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (evt.X is null || evt.Y is null) return false;
            var virtualScreen = ScreenGeometry.VirtualBounds();
            var pixelX = _bounds.NormalizedXToPixel(evt.X.Value);
            var pixelY = _bounds.NormalizedYToPixel(evt.Y.Value);
            var normalizedX = (int)Math.Round((pixelX - virtualScreen.X) * 65535.0 / Math.Max(1, virtualScreen.Width - 1));
            var normalizedY = (int)Math.Round((pixelY - virtualScreen.Y) * 65535.0 / Math.Max(1, virtualScreen.Height - 1));
            normalizedX = Math.Clamp(normalizedX, 0, 65535);
            normalizedY = Math.Clamp(normalizedY, 0, 65535);

            var abs = SendInputNative.CreateMouse(
                normalizedX, normalizedY, 0,
                SendInputNative.MouseEventfMove |
                SendInputNative.MouseEventfAbsolute |
                SendInputNative.MouseEventfVirtualDesk);
            return SendInputNative.Send(ref abs) == 1;
        }

        var dx = (int)Math.Round(evt.Dx ?? 0);
        var dy = (int)Math.Round(evt.Dy ?? 0);
        if (dx == 0 && dy == 0) return true;

        var move = SendInputNative.CreateMouse(dx, dy, 0, SendInputNative.MouseEventfMove);
        return SendInputNative.Send(ref move) == 1;
    }

    private static bool InjectMouseButton(InputEvent evt)
    {
        var flag = (evt.Button?.ToLowerInvariant(), evt.Action?.ToLowerInvariant()) switch
        {
            ("left", "down") => SendInputNative.MouseEventfLeftDown,
            ("left", "up") => SendInputNative.MouseEventfLeftUp,
            ("right", "down") => SendInputNative.MouseEventfRightDown,
            ("right", "up") => SendInputNative.MouseEventfRightUp,
            ("middle", "down") => SendInputNative.MouseEventfMiddleDown,
            ("middle", "up") => SendInputNative.MouseEventfMiddleUp,
            ("x1", "down") => SendInputNative.MouseEventfXDown,
            ("x1", "up") => SendInputNative.MouseEventfXUp,
            ("x2", "down") => SendInputNative.MouseEventfXDown,
            ("x2", "up") => SendInputNative.MouseEventfXUp,
            _ => 0u
        };
        if (flag == 0) return false;

        uint data = evt.Button?.ToLowerInvariant() == "x2"
            ? SendInputNative.XButton2
            : SendInputNative.XButton1;
        if ((flag & (SendInputNative.MouseEventfXDown | SendInputNative.MouseEventfXUp)) == 0)
            data = 0;

        var input = SendInputNative.CreateMouse(0, 0, data, flag);
        return SendInputNative.Send(ref input) == 1;
    }

    private static bool InjectMouseWheel(InputEvent evt)
    {
        var vertical = evt.WheelDelta ?? 0;
        var horizontal = evt.WheelDeltaX ?? 0;
        var ok = true;

        if (vertical != 0)
        {
            var input = SendInputNative.CreateMouse(0, 0, unchecked((uint)vertical), SendInputNative.MouseEventfWheel);
            ok &= SendInputNative.Send(ref input) == 1;
        }

        if (horizontal != 0)
        {
            var input = SendInputNative.CreateMouse(0, 0, unchecked((uint)horizontal), SendInputNative.MouseEventfHWheel);
            ok &= SendInputNative.Send(ref input) == 1;
        }

        return ok;
    }

    private bool InjectText(InputEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Key)) return false;
        var ok = true;
        foreach (var ch in evt.Key)
        {
            var down = SendInputNative.CreateKeyboard(ch, SendInputNative.KeyEventfUnicode);
            var up = SendInputNative.CreateKeyboard(ch, SendInputNative.KeyEventfUnicode | SendInputNative.KeyEventfKeyup);
            ok &= SendInputNative.Send(ref down) == 1;
            ok &= SendInputNative.Send(ref up) == 1;
        }
        return ok;
    }

    /// <summary>
    /// Single-finger fallback for machines with no touch digitizer. Touch Injection
    /// cannot work there (ERROR_INVALID_PARAMETER 87), but users still expect tap and
    /// drag to control Windows. Multi-finger gestures require vmulti hardware.
    /// </summary>
    private bool InjectTouchAsMouse(InputEvent evt)
    {
        if (!_operational) return false;

        if (string.Equals(evt.Action, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (!_fallbackMouseDown) return true;
            SendButton(SendInputNative.MouseEventfLeftUp);
            _fallbackMouseDown = false;
            _fallbackTouchId = null;
            return true;
        }

        if (evt.Touches is null || evt.Touches.Count == 0) return true;

        var ok = true;
        foreach (var touch in evt.Touches)
        {
            var phase = touch.Phase.ToLowerInvariant();
            if (phase == "down")
            {
                if (_fallbackTouchId is not null) continue; // first finger owns the mouse
                _fallbackTouchId = touch.Id;
                _fallbackX = touch.X;
                _fallbackY = touch.Y;
                ok &= MoveMouseAbsolute(touch.X, touch.Y);
                ok &= SendButton(SendInputNative.MouseEventfLeftDown);
                _fallbackMouseDown = true;
            }
            else if (phase == "move")
            {
                if (_fallbackTouchId != touch.Id || !_fallbackMouseDown) continue;
                _fallbackX = touch.X;
                _fallbackY = touch.Y;
                ok &= MoveMouseAbsolute(touch.X, touch.Y);
            }
            else if (phase is "up" or "cancel")
            {
                if (_fallbackTouchId != touch.Id) continue;
                ok &= MoveMouseAbsolute(_fallbackX, _fallbackY);
                if (_fallbackMouseDown) ok &= SendButton(SendInputNative.MouseEventfLeftUp);
                _fallbackMouseDown = false;
                _fallbackTouchId = null;
            }
        }
        return ok;
    }

    private bool MoveMouseAbsolute(double nx, double ny)
    {
        var virtualScreen = ScreenGeometry.VirtualBounds();
        var pixelX = _bounds.NormalizedXToPixel(nx);
        var pixelY = _bounds.NormalizedYToPixel(ny);
        var x = (int)Math.Round((pixelX - virtualScreen.X) * 65535.0 / Math.Max(1, virtualScreen.Width - 1));
        var y = (int)Math.Round((pixelY - virtualScreen.Y) * 65535.0 / Math.Max(1, virtualScreen.Height - 1));
        var input = SendInputNative.CreateMouse(
            Math.Clamp(x, 0, 65535), Math.Clamp(y, 0, 65535), 0,
            SendInputNative.MouseEventfMove |
            SendInputNative.MouseEventfAbsolute |
            SendInputNative.MouseEventfVirtualDesk);
        return SendInputNative.Send(ref input) == 1;
    }

    private static bool SendButton(uint flag)
    {
        var input = SendInputNative.CreateMouse(0, 0, 0, flag);
        return SendInputNative.Send(ref input) == 1;
    }

    private bool InjectTouch(InputEvent evt)
    {
        if (!_touchInitialized)
            return InjectTouchAsMouse(evt);

        lock (_sync)
        {
            if (string.Equals(evt.Action, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                var cancelled = new List<NativeTouchInjection.PointerTouchInfo>();
                foreach (var pair in _activeTouches)
                {
                    // An UP frame must carry the exact last pixel position, otherwise
                    // InjectTouchInput returns ERROR_INVALID_PARAMETER and drops all contacts.
                    cancelled.Add(BuildTouchInfo(pair.Key, pair.Value.X, pair.Value.Y, NativeTouchInjection.PointerFlagUp));
                }
                if (cancelled.Count == 0) return true;
                var cancelInjected = InjectTouchWithRetry((uint)cancelled.Count, cancelled.ToArray());
                if (cancelInjected) _activeTouches.Clear();
                return cancelInjected;
            }

            if (evt.Touches is null || evt.Touches.Count == 0)
                return true;

            var down = new HashSet<int>();
            var released = new Dictionary<int, TouchContact>();

            foreach (var touch in evt.Touches)
            {
                var phase = touch.Phase.ToLowerInvariant();
                if (phase is "up" or "cancel")
                {
                    // UP must reuse the exact last pixel location of that contact.
                    if (_activeTouches.TryGetValue(touch.Id, out var last))
                        released[touch.Id] = last;
                    continue;
                }

                var existed = _activeTouches.ContainsKey(touch.Id);
                if (!existed && _activeTouches.Count >= MaxTouchContacts)
                    continue; // Never exceed InitializeTouchInjection's contact limit.
                _activeTouches[touch.Id] = touch.Clone();
                if (phase == "down" || !existed) down.Add(touch.Id);
            }

            if (_activeTouches.Count == 0 && released.Count == 0) return true;

            // Windows touch injection models all contacts on the digitizer as a
            // single frame. A second finger MUST be submitted together with every
            // still-active contact; sending only the new contact overwrites the
            // existing frame and multi-touch appears dead. This is the critical
            // difference from mouse injection.
            var contacts = new List<NativeTouchInjection.PointerTouchInfo>(_activeTouches.Count + released.Count);
            foreach (var pair in _activeTouches.OrderBy(pair => pair.Key))
            {
                if (released.ContainsKey(pair.Key)) continue;
                var flags = down.Contains(pair.Key)
                    ? NativeTouchInjection.PointerFlagDown |
                      NativeTouchInjection.PointerFlagInRange |
                      NativeTouchInjection.PointerFlagInContact
                    : NativeTouchInjection.PointerFlagUpdate |
                      NativeTouchInjection.PointerFlagInRange |
                      NativeTouchInjection.PointerFlagInContact;
                contacts.Add(BuildTouchInfo(pair.Key, pair.Value.X, pair.Value.Y, flags));
            }

            foreach (var pair in released)
            {
                contacts.Add(BuildTouchInfo(pair.Key, pair.Value.X, pair.Value.Y, NativeTouchInjection.PointerFlagUp));
            }

            if (contacts.Count == 0) return true;
            if (contacts.Count > MaxTouchContacts)
                contacts.RemoveRange(MaxTouchContacts, contacts.Count - MaxTouchContacts);

            var injected = InjectTouchWithRetry((uint)contacts.Count, contacts.ToArray());
            if (injected)
            {
                foreach (var id in released.Keys) _activeTouches.Remove(id);
            }
            return injected;
        }
    }

    private bool InjectTouchWithRetry(uint count, NativeTouchInjection.PointerTouchInfo[] contacts)
    {
        // ERROR_NOT_READY (21) means two frames landed in the same 0.1ms timestamp
        // window. Retrying the same frame shortly is explicitly supported by the API.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeTouchInjection.InjectTouchInput(count, contacts))
                return true;

            var error = Marshal.GetLastWin32Error();
            if (error != 21 /* ERROR_NOT_READY */)
            {
                LogTouchError(error);
                return false;
            }

            Thread.Sleep(2);
        }

        LogTouchError(Marshal.GetLastWin32Error());
        return false;
    }

    private void LogTouchError(int error)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTouchErrorLogTicks) <= 2000) return;
        Interlocked.Exchange(ref _lastTouchErrorLogTicks, now);
        _logger.LogWarning("InjectTouchInput failed: {Error} (Win32={Code})",
            new Win32Exception(error).Message, error);
    }

    private NativeTouchInjection.PointerTouchInfo BuildTouchInfo(int id, double nx, double ny, uint flags)
    {
        var screen = ScreenGeometry.VirtualBounds();
        var right = screen.X + Math.Max(1, screen.Width) - 1;
        var bottom = screen.Y + Math.Max(1, screen.Height) - 1;

        // Important: InjectTouchInput expects physical screen coordinates in pixels.
        // It does NOT use the 0..65535 normalized coordinate space of SendInput.
        var pixelX = Math.Clamp((int)Math.Round(_bounds.NormalizedXToPixel(nx)), screen.X, right);
        var pixelY = Math.Clamp((int)Math.Round(_bounds.NormalizedYToPixel(ny)), screen.Y, bottom);

        return new NativeTouchInjection.PointerTouchInfo
        {
            PointerType = NativeTouchInjection.PtTouch,
            PointerId = (uint)(id + 1),
            PointerFlags = flags,
            PixelX = pixelX,
            PixelY = pixelY,
            HimetricX = 0,
            HimetricY = 0,
            PixelXRaw = pixelX,
            PixelYRaw = pixelY,
            HimetricXRaw = 0,
            HimetricYRaw = 0,
            TouchFlags = 0,
            TouchMask = NativeTouchInjection.TouchMaskContactArea |
                        NativeTouchInjection.TouchMaskOrientation |
                        NativeTouchInjection.TouchMaskPressure,
            ContactLeft = Math.Clamp(pixelX - 2, screen.X, right),
            ContactTop = Math.Clamp(pixelY - 2, screen.Y, bottom),
            ContactRight = Math.Clamp(pixelX + 2, screen.X, right),
            ContactBottom = Math.Clamp(pixelY + 2, screen.Y, bottom),
            ContactRawLeft = Math.Clamp(pixelX - 2, screen.X, right),
            ContactRawTop = Math.Clamp(pixelY - 2, screen.Y, bottom),
            ContactRawRight = Math.Clamp(pixelX + 2, screen.X, right),
            ContactRawBottom = Math.Clamp(pixelY + 2, screen.Y, bottom),
            Orientation = 0,
            Pressure = 32000
        };
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            if (_fallbackMouseDown)
            {
                SendButton(SendInputNative.MouseEventfLeftUp);
                _fallbackMouseDown = false;
            }

            if (_touchInitialized && _activeTouches.Count > 0)
            {
                var ups = _activeTouches
                    .Select(pair => BuildTouchInfo(pair.Key, pair.Value.X, pair.Value.Y, NativeTouchInjection.PointerFlagUp))
                    .ToArray();
                InjectTouchWithRetry((uint)ups.Length, ups);
                _activeTouches.Clear();
            }
            _touchInitialized = false;
            _operational = false;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();
}
