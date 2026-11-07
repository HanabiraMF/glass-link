using System.ComponentModel;
using System.Runtime.InteropServices;
using GlassLink.Protocol;
using Microsoft.Extensions.Logging;

namespace GlassLink.Input.Hardware;

/// <summary>
/// Driver-level injection layer.
///
/// Keyboard + mouse: Interception kernel HID filter driver, scan-code level,
///   relative mouse gestures for game-view rotation, absolute support for remote desktop.
/// Multi-touch: vmulti-win11 virtual HID multitouch device through the
///   vmulticlient native API (20 contacts, normalized 0..32767).
///
/// This layer does NOT use SendInput and is the preferred layer for full-screen
/// DirectX / Unity / Unreal games and Secure Desktop/UAC scenarios.
/// </summary>
public sealed class HardwareInputController : IInputController
{
    private readonly ILogger<HardwareInputController> _logger;
    private readonly object _sync = new();
    private IntPtr _vmulti;
    private InterceptionSession? _interception;
    private CaptureBounds _bounds;
    private bool _operational;

    private readonly Dictionary<int, byte> _touchSlots = new();
    private readonly Dictionary<byte, TouchContact> _activeTouches = new();
    private readonly HashSet<byte> _usedSlots = new();
    private long _lastErrorLogTicks;

    public HardwareInputController(ILogger<HardwareInputController> logger)
    {
        _logger = logger;
        _bounds = ScreenGeometry.PrimaryBounds();
    }

    public string LayerId => ControllerLayerIds.Hardware;
    public string DisplayName => "虚拟硬件层（Interception + vmulti-win11）";
    public CaptureBounds Bounds => _bounds;
    public bool IsOperational => _operational;
    public bool TouchAvailable => _vmulti != IntPtr.Zero;
    public bool KeyboardMouseAvailable => _interception is not null;

    public async Task<ControllerActivation> ActivateAsync(CaptureBounds bounds, CancellationToken cancellationToken)
    {
        await Task.Yield();
        lock (_sync)
        {
            if (_operational)
            {
                _bounds = bounds;
                return new ControllerActivation(true, LayerId, LayerId);
            }

            _bounds = bounds.Width > 0 && bounds.Height > 0 ? bounds : ScreenGeometry.PrimaryBounds();
            var diagnostics = new List<string>();
            var failures = new List<string>();

            _interception = InterceptionSession.TryCreate(_logger, out var interceptionError, out var interceptionDiagnostics);
            diagnostics.AddRange(interceptionDiagnostics);
            if (_interception is null)
                failures.Add(interceptionError ?? "Interception 不可用。");

            try
            {
                _vmulti = VMultiNative.vmulti_alloc();
                if (_vmulti == IntPtr.Zero)
                {
                    failures.Add("vmulti_alloc 返回空指针。");
                }
                else if (!VMultiNative.vmulti_connect(_vmulti))
                {
                    failures.Add("vmulti_connect 失败：未找到 vmulti-win11 虚拟 HID 设备。");
                    VMultiNative.vmulti_free(_vmulti);
                    _vmulti = IntPtr.Zero;
                }
                else
                {
                    diagnostics.Add("vmulti-win11 多指虚拟触摸屏已连接（最多 20 触点）。");
                    diagnostics.Add("请用 testvmulti.exe /multitouch 验证多指坐标映射与 Win32k 触摸消息链路。");
                }
            }
            catch (DllNotFoundException)
            {
                failures.Add("找不到 vmulticlient.dll。请使用 native/vmulticlient-shim/build.ps1 构建并放到 native\\ 目录。");
            }
            catch (BadImageFormatException)
            {
                failures.Add("vmulticlient.dll 架构不匹配；必须使用 x64 版本。");
            }
            catch (Exception ex)
            {
                failures.Add($"vmulti 初始化异常：{ex.Message}");
            }

            if (_interception is null && _vmulti == IntPtr.Zero)
            {
                CleanupLocked();
                var reason = string.Join("；", failures);
                diagnostics.Add("虚拟硬件层初始化失败：" + reason);
                diagnostics.Add("已回退到软件层。请以管理员身份安装 Interception 和/或 vmulti 驱动。");
                _logger.LogWarning("Virtual hardware layer unavailable: {Reason}", reason);
                return new ControllerActivation(false, ControllerLayerIds.Software, ControllerLayerIds.Hardware, reason, diagnostics);
            }

            if (_interception is null)
            {
                diagnostics.Add("vmulti 触摸已就绪；Interception 不可用，键鼠将单独回退软件层。");
                _logger.LogWarning("vmulti touch active without Interception; keyboard/mouse fall back to software.");
            }
            else if (_vmulti == IntPtr.Zero)
            {
                diagnostics.Add("Interception 键鼠硬件层已就绪；vmulti 触摸不可用，触摸将自动回退软件层。");
                _logger.LogWarning("Hardware keyboard/mouse active without vmulti touch; touch falls back to software.");
            }
            else
            {
                diagnostics.Add("虚拟硬件层就绪；键盘/鼠标/多点触摸均使用硬件注入。");
            }
            _operational = true;
            _logger.LogInformation("Virtual hardware input layer activated. Bounds={X},{Y} {W}x{H}",
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);

            return new ControllerActivation(true, LayerId, LayerId, null, diagnostics);
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
        lock (_sync)
        {
            if (_interception is null && _vmulti == IntPtr.Zero) return false;

            try
            {
                if (!string.Equals(evt.Type, InputTypeNames.Touch, StringComparison.OrdinalIgnoreCase) &&
                    _interception is null)
                    return false;

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
                LogRateLimited(ex, "Virtual hardware injection failed");
                return false;
            }
        }
    }

    private bool InjectKeyboard(InputEvent evt)
    {
        if (!KeyMap.TryResolve(evt, out var key)) return false;
        var down = string.Equals(evt.Action, "down", StringComparison.OrdinalIgnoreCase);
        var up = string.Equals(evt.Action, "up", StringComparison.OrdinalIgnoreCase);
        if (!down && !up) return false;
        return _interception!.SendKey((ushort)key.ScanCode, down, key.Extended);
    }

    private bool InjectText(InputEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Key)) return false;
        var allOk = true;

        foreach (var character in evt.Key)
        {
            if (character == '\r') continue;
            if (character == '\n')
            {
                allOk &= _interception!.SendKey(0x1C, true, false);
                allOk &= _interception.SendKey(0x1C, false, false);
                continue;
            }

            if (!KeyMap.TryResolveCharacter(character, out var key, out var layoutModifiers))
            {
                allOk = false;
                _logger.LogDebug("Virtual keyboard character U+{Code:X4} is not reachable via the current keyboard layout.", (int)character);
                continue;
            }

            var shift = (layoutModifiers & 1) != 0;
            var ctrl = (layoutModifiers & 2) != 0;
            var alt = (layoutModifiers & 4) != 0;

            if (shift) allOk &= _interception!.SendKey(0x2A, true, false);  // Left Shift
            if (ctrl) allOk &= _interception!.SendKey(0x1D, true, false);  // Left Ctrl
            if (alt) allOk &= _interception!.SendKey(0x38, true, false);   // Left Alt

            allOk &= _interception!.SendKey((ushort)key.ScanCode, true, key.Extended);
            allOk &= _interception!.SendKey((ushort)key.ScanCode, false, key.Extended);

            if (alt) allOk &= _interception!.SendKey(0x38, false, false);
            if (ctrl) allOk &= _interception!.SendKey(0x1D, false, false);
            if (shift) allOk &= _interception!.SendKey(0x2A, false, false);
        }

        return allOk;
    }

    private bool InjectMouseMove(InputEvent evt)
    {
        if (string.Equals(evt.Mode, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (evt.X is null || evt.Y is null) return false;
            var screen = ScreenGeometry.VirtualBounds();
            var pixelX = _bounds.NormalizedXToPixel(evt.X.Value);
            var pixelY = _bounds.NormalizedYToPixel(evt.Y.Value);
            var x = (int)Math.Round((pixelX - screen.X) * 65535.0 / Math.Max(1, screen.Width - 1));
            var y = (int)Math.Round((pixelY - screen.Y) * 65535.0 / Math.Max(1, screen.Height - 1));
            var stroke = new InterceptionNative.MouseStroke
            {
                State = 0,
                Flags = InterceptionNative.MouseMoveAbsolute | InterceptionNative.MouseVirtualDesktop,
                X = Math.Clamp(x, 0, 65535),
                Y = Math.Clamp(y, 0, 65535),
                Rolling = 0,
                Information = 0
            };
            return _interception!.SendMouse(stroke);
        }

        var dx = (int)Math.Round(evt.Dx ?? 0);
        var dy = (int)Math.Round(evt.Dy ?? 0);
        if (dx == 0 && dy == 0) return true;
        return _interception!.SendMouse(new InterceptionNative.MouseStroke
        {
            State = 0,
            Flags = InterceptionNative.MouseMoveRelative,
            X = dx,
            Y = dy,
            Rolling = 0,
            Information = 0
        });
    }

    private bool InjectMouseButton(InputEvent evt)
    {
        var state = (evt.Button?.ToLowerInvariant(), evt.Action?.ToLowerInvariant()) switch
        {
            ("left", "down") => InterceptionNative.MouseLeftDown,
            ("left", "up") => InterceptionNative.MouseLeftUp,
            ("right", "down") => InterceptionNative.MouseRightDown,
            ("right", "up") => InterceptionNative.MouseRightUp,
            ("middle", "down") => InterceptionNative.MouseMiddleDown,
            ("middle", "up") => InterceptionNative.MouseMiddleUp,
            ("x1", "down") => InterceptionNative.MouseButton4Down,
            ("x1", "up") => InterceptionNative.MouseButton4Up,
            ("x2", "down") => InterceptionNative.MouseButton5Down,
            ("x2", "up") => InterceptionNative.MouseButton5Up,
            _ => (ushort)0
        };
        if (state == 0) return false;

        return _interception!.SendMouse(new InterceptionNative.MouseStroke
        {
            State = state,
            Flags = InterceptionNative.MouseMoveRelative,
            X = 0,
            Y = 0,
            Rolling = 0,
            Information = 0
        });
    }

    private bool InjectMouseWheel(InputEvent evt)
    {
        var ok = true;
        if (evt.WheelDelta is { } vertical && vertical != 0)
        {
            ok &= _interception!.SendMouse(new InterceptionNative.MouseStroke
            {
                State = InterceptionNative.MouseWheel,
                Flags = InterceptionNative.MouseMoveRelative,
                Rolling = (short)Math.Clamp(vertical, short.MinValue, short.MaxValue)
            });
        }
        if (evt.WheelDeltaX is { } horizontal && horizontal != 0)
        {
            ok &= _interception!.SendMouse(new InterceptionNative.MouseStroke
            {
                State = InterceptionNative.MouseHWheel,
                Flags = InterceptionNative.MouseMoveRelative,
                Rolling = (short)Math.Clamp(horizontal, short.MinValue, short.MaxValue)
            });
        }
        return ok;
    }

    private bool InjectTouch(InputEvent evt)
    {
        if (_vmulti == IntPtr.Zero) return false;

        if (string.Equals(evt.Action, "cancel", StringComparison.OrdinalIgnoreCase))
            return ReleaseAllTouchesLocked();

        if (evt.Touches is null || evt.Touches.Count == 0)
            return true;

        var report = new List<VMultiNative.Touch>(VMultiNative.MultiMaxCount);
        var releasing = new List<byte>();

        foreach (var touch in evt.Touches)
        {
            var phase = touch.Phase.ToLowerInvariant();
            if (phase is "down" or "move")
            {
                var slot = GetOrCreateSlot(touch.Id);
                _activeTouches[slot] = touch.Clone();
            }
        }

        foreach (var touch in evt.Touches)
        {
            var phase = touch.Phase.ToLowerInvariant();
            if (phase is "up" or "cancel")
            {
                if (!_touchSlots.TryGetValue(touch.Id, out var slot)) continue;
                var last = _activeTouches.TryGetValue(slot, out var existing) ? existing : touch;
                report.Add(BuildTouch(slot, last.X, last.Y, released: true));
                releasing.Add(slot);
            }
        }

        foreach (var (slot, touch) in _activeTouches.OrderBy(pair => pair.Key))
        {
            if (releasing.Contains(slot)) continue;
            report.Add(BuildTouch(slot, touch.X, touch.Y, released: false));
        }

        if (report.Count == 0) return true;
        if (report.Count > VMultiNative.MultiMaxCount)
            report.RemoveRange(VMultiNative.MultiMaxCount, report.Count - VMultiNative.MultiMaxCount);

        var sent = VMultiNative.vmulti_update_multitouch(
            _vmulti,
            report.ToArray(),
            (byte)report.Count);

        if (sent)
        {
            foreach (var touch in evt.Touches.Where(t => t.Phase is "up" or "cancel"))
            {
                if (!_touchSlots.Remove(touch.Id, out var slot)) continue;
                _activeTouches.Remove(slot);
                _usedSlots.Remove(slot);
            }
        }

        return sent;
    }

    private VMultiNative.Touch BuildTouch(byte slot, double nx, double ny, bool released)
    {
        var screen = ScreenGeometry.VirtualBounds();
        var pixelX = _bounds.NormalizedXToPixel(nx);
        var pixelY = _bounds.NormalizedYToPixel(ny);
        var x = (int)Math.Round((pixelX - screen.X) * (double)VMultiNative.MultiMaxCoordinate / Math.Max(1, screen.Width - 1));
        var y = (int)Math.Round((pixelY - screen.Y) * (double)VMultiNative.MultiMaxCoordinate / Math.Max(1, screen.Height - 1));

        return new VMultiNative.Touch
        {
            Status = released
                ? (byte)0
                : (byte)(VMultiNative.MultiTipSwitchBit | VMultiNative.MultiInRangeBit | VMultiNative.MultiConfidenceBit),
            ContactId = slot,
            XValue = (ushort)Math.Clamp(x, 0, VMultiNative.MultiMaxCoordinate),
            YValue = (ushort)Math.Clamp(y, 0, VMultiNative.MultiMaxCoordinate),
            Width = 24,
            Height = 24
        };
    }

    private byte GetOrCreateSlot(int pointerId)
    {
        if (_touchSlots.TryGetValue(pointerId, out var existing)) return existing;

        for (byte slot = 0; slot < VMultiNative.MultiMaxCount; slot++)
        {
            if (_usedSlots.Contains(slot)) continue;
            _usedSlots.Add(slot);
            _touchSlots[pointerId] = slot;
            return slot;
        }

        // More than 20 simultaneous pointers is outside the vmulti HID report
        // capacity. Reuse slot 0 while keeping the dictionary consistent.
        var fallback = (byte)(pointerId % VMultiNative.MultiMaxCount);
        _touchSlots[pointerId] = fallback;
        _usedSlots.Add(fallback);
        return fallback;
    }

    private bool ReleaseAllTouchesLocked()
    {
        if (_activeTouches.Count == 0) return true;
        var report = _activeTouches
            .OrderBy(pair => pair.Key)
            .Select(pair => BuildTouch(pair.Key, pair.Value.X, pair.Value.Y, released: true))
            .ToArray();
        var sent = VMultiNative.vmulti_update_multitouch(_vmulti, report, (byte)report.Length);
        _activeTouches.Clear();
        _touchSlots.Clear();
        _usedSlots.Clear();
        return sent;
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            CleanupLocked();
        }
        return Task.CompletedTask;
    }

    private void CleanupLocked()
    {
        _operational = false;
        if (_vmulti != IntPtr.Zero)
        {
            try
            {
                if (_activeTouches.Count > 0) ReleaseAllTouchesLocked();
                VMultiNative.vmulti_disconnect(_vmulti);
            }
            catch
            {
                // ignore native teardown failures
            }
            finally
            {
                try { VMultiNative.vmulti_free(_vmulti); } catch { }
                _vmulti = IntPtr.Zero;
            }
        }

        _interception?.Dispose();
        _interception = null;
        _activeTouches.Clear();
        _touchSlots.Clear();
        _usedSlots.Clear();
    }

    private void LogRateLimited(Exception ex, string message)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastErrorLogTicks);
        if (now - previous < 2000) return;
        Interlocked.Exchange(ref _lastErrorLogTicks, now);
        _logger.LogWarning(ex, "{Message}", message);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();
}
