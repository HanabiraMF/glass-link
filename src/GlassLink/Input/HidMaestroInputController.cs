using System.Reflection;
using GlassLink.Protocol;
using HIDMaestro;
using Microsoft.Extensions.Logging;

namespace GlassLink.Input;

/// <summary>
/// HIDMaestro UMDF2 virtual keyboard/mouse. The driver is installed from the
/// signed embedded payload on first use; no Interception or test signing.
/// GlassLink requires administrator at startup, so device creation is allowed.
/// </summary>
public sealed class HidMaestroInputController : IInputController
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private HMContext? _context;
    private HMController? _mouse;
    private HMController? _keyboard;
    private HMController? _gamepad;
    private CaptureBounds _bounds;
    private bool _operational;
    private HMMouseButtons _buttons;
    private double? _lastX, _lastY;
    private HMKeyModifiers _modifiers;
    private readonly HashSet<HMKey> _keys = new();
    private static readonly string[] KeyNames = Enum.GetNames<HMKey>();

    public HidMaestroInputController(ILogger logger) => _logger = logger;

    public string LayerId => ControllerLayerIds.Hardware;
    public string DisplayName => "HIDMaestro 驱动注入（键盘/鼠标）";
    public CaptureBounds Bounds => _bounds;
    public bool IsOperational => _operational;

    public async Task<ControllerActivation> ActivateAsync(CaptureBounds bounds, CancellationToken cancellationToken)
    {
        _bounds = bounds;
        var diagnostics = new List<string>
        {
            "HIDMaestro UMDF2 虚拟 HID，无内核驱动、无需测试模式。",
            "首次启动自动安装本地受信任驱动。"
        };

        try
        {
            await Task.Run(() =>
            {
                _context = new HMContext();
                _context.LoadDefaultProfiles();
                _context.InstallDriver();
                // Use a generic relative mouse. Cloning a Logitech G502 (same
                // VID/PID family as many physical mice) can make Windows merge
                // the virtual and real mouse collections.
                _mouse = _context.CreateController(_context.GetProfile("microsoft-classic-intellimouse"), "glasslink-mouse");
                _keyboard = _context.CreateController(_context.GetProfile("keychron-q6"), "glasslink-keyboard");
                try
                {
                    _gamepad = _context.CreateController(_context.GetProfile("xbox-360-wired"), "glasslink-gamepad");
                    _gamepad.SubmitState(new HMGamepadState());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Virtual Xbox controller could not be created; keyboard/mouse remain active.");
                }
            }, cancellationToken);

            if (_mouse is null || _keyboard is null)
                throw new InvalidOperationException("HIDMaestro profile/device creation failed.");

            _operational = true;
            diagnostics.Add("虚拟鼠标：logitech-g502-hero；虚拟键盘：keychron-q6。");
            _logger.LogInformation("HIDMaestro input layer active.");
            return new ControllerActivation(true, LayerId, DisplayName, null, diagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HIDMaestro unavailable; falling back to the next input layer.");
            try { Dispose(); } catch { }
            return new ControllerActivation(false, ControllerLayerIds.Software, LayerId,
                "HIDMaestro 驱动注入不可用：" + ex.Message, diagnostics);
        }
    }

    public void UpdateBounds(CaptureBounds bounds)
    {
        if (bounds.Width > 0 && bounds.Height > 0) _bounds = bounds;
    }

    public bool Inject(InputEvent evt)
    {
        lock (_sync)
        {
            if (!_operational || _mouse is null || _keyboard is null) return false;
            try
            {
                return evt.Type switch
                {
                    InputTypeNames.MouseMove => MouseMove(evt),
                    InputTypeNames.MouseButton => MouseButton(evt),
                    InputTypeNames.MouseWheel => Wheel(evt),
                    InputTypeNames.Keyboard => Keyboard(evt),
                    InputTypeNames.Text => Text(evt),
                    InputTypeNames.Gamepad => Gamepad(evt),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HIDMaestro injection failed for {Type}.", evt.Type);
                return false;
            }
        }
    }

    private bool MouseMove(InputEvent evt)
    {
        if (string.Equals(evt.Mode, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (evt.X is null || evt.Y is null) return false;
            if (_lastX is null) { _lastX = evt.X; _lastY = evt.Y; return true; }
            var dx = (int)Math.Round((evt.X.Value - _lastX.Value) * Math.Max(1, _bounds.Width));
            var dy = (int)Math.Round((evt.Y.Value - _lastY.Value) * Math.Max(1, _bounds.Height));
            _lastX = evt.X; _lastY = evt.Y;
            if (dx == 0 && dy == 0) return true;
            _mouse!.SubmitMouse(new HMMouseState { X = dx, Y = dy, Buttons = _buttons });
            return true;
        }

        var mx = (int)Math.Round(evt.Dx ?? 0);
        var my = (int)Math.Round(evt.Dy ?? 0);
        if (mx == 0 && my == 0) return true;
        _mouse!.SubmitMouse(new HMMouseState { X = mx, Y = my, Buttons = _buttons });
        return true;
    }

    private bool MouseButton(InputEvent evt)
    {
        var flag = evt.Button?.ToLowerInvariant() switch
        {
            "left" => HMMouseButtons.Left,
            "right" => HMMouseButtons.Right,
            "middle" => HMMouseButtons.Middle,
            _ => (HMMouseButtons)0
        };
        if (flag == 0) return false;
        if (string.Equals(evt.Action, "down", StringComparison.OrdinalIgnoreCase)) _buttons |= flag;
        else if (string.Equals(evt.Action, "up", StringComparison.OrdinalIgnoreCase)) _buttons &= ~flag;
        else return false;
        _mouse!.SubmitMouse(new HMMouseState { Buttons = _buttons });
        return true;
    }

    private bool Wheel(InputEvent evt)
    {
        var delta = (evt.WheelDelta ?? 0) + (evt.WheelDeltaX ?? 0);
        if (delta == 0) return true;
        _mouse!.SubmitMouse(new HMMouseState { Wheel = delta, Buttons = _buttons });
        return true;
    }

    private bool Keyboard(InputEvent evt)
    {
        var down = string.Equals(evt.Action, "down", StringComparison.OrdinalIgnoreCase);
        var up = string.Equals(evt.Action, "up", StringComparison.OrdinalIgnoreCase);
        if (!down && !up) return false;
        if (TryModifier(evt.Code, out var modifier))
        {
            _modifiers = down ? (_modifiers | modifier) : (_modifiers & ~modifier);
            _keyboard!.SubmitKeyboard(_modifiers, _keys.ToArray());
            return true;
        }
        if (!TryKey(evt.Code, out var key)) return false;
        if (down) _keys.Add(key); else _keys.Remove(key);
        _keyboard!.SubmitKeyboard(_modifiers, _keys.ToArray());
        return true;
    }

    private bool Gamepad(InputEvent evt)
    {
        if (_gamepad is null || evt.Gamepad is null) return false;
        var state = new HMGamepadState();
        foreach (var name in evt.Gamepad.Buttons)
        {
            var mapped = name.ToUpperInvariant() switch
            {
                "L1" => HMButton.LeftBumper,
                "R1" => HMButton.RightBumper,
                "L2" => HMButton.LeftTriggerClick,
                "R2" => HMButton.RightTriggerClick,
                "L3" => HMButton.LeftStick,
                "R3" => HMButton.RightStick,
                "START" => HMButton.Start,
                "SELECT" => HMButton.Back,
                _ => Enum.TryParse<HMButton>(name, true, out var b) ? b : HMButton.None
            };
            state.Buttons |= mapped;
            if (mapped == HMButton.LeftTriggerClick) state.Axes[HMAxis.Z] = 1f;
            if (mapped == HMButton.RightTriggerClick) state.Axes[HMAxis.Rz] = 1f;
        }
        state.Axes[HMAxis.X] = (float)Math.Clamp(evt.Gamepad.LeftX, -1, 1);
        state.Axes[HMAxis.Y] = (float)-Math.Clamp(evt.Gamepad.LeftY, -1, 1);
        state.Axes[HMAxis.Rx] = (float)Math.Clamp(evt.Gamepad.RightX, -1, 1);
        state.Axes[HMAxis.Ry] = (float)-Math.Clamp(evt.Gamepad.RightY, -1, 1);
        if (!state.Axes.ContainsKey(HMAxis.Z)) state.Axes[HMAxis.Z] = (float)Math.Clamp(evt.Gamepad.LeftTrigger, 0, 1);
        if (!state.Axes.ContainsKey(HMAxis.Rz)) state.Axes[HMAxis.Rz] = (float)Math.Clamp(evt.Gamepad.RightTrigger, 0, 1);
        _gamepad.SubmitState(state);
        return true;
    }

    private bool Text(InputEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Key)) return false;
        foreach (var ch in evt.Key)
        {
            if (ch is '\r' or '\n') continue;
            var name = char.IsLetter(ch) ? char.ToUpperInvariant(ch).ToString()
                : char.IsDigit(ch) ? "D" + ch : ch.ToString();
            if (!TryKey(name, out var key)) continue;
            _keyboard!.SubmitKeyboard(HMKeyModifiers.None, new[] { key });
            _keyboard!.SubmitKeyboard(HMKeyModifiers.None, Array.Empty<HMKey>());
        }
        return true;
    }

    private static bool TryKey(string? code, out HMKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(code)) return false;
        var target = code switch
        {
            "ArrowUp" => "Up",
            "ArrowDown" => "Down",
            "ArrowLeft" => "Left",
            "ArrowRight" => "Right",
            "Equal" => "Equals",
            "Quote" => "Apostrophe",
            "Backquote" => "Grave",
            "BracketLeft" => "LeftBracket",
            "BracketRight" => "RightBracket",
            "NumpadAdd" => "KeypadPlus",
            "NumpadSubtract" => "KeypadMinus",
            "NumpadMultiply" => "KeypadMultiply",
            "NumpadDivide" => "KeypadDivide",
            "NumpadDecimal" => "KeypadPeriod",
            _ when code.StartsWith("Key", StringComparison.OrdinalIgnoreCase) => code[3..],
            _ when code.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) => code[5..],
            _ when code.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) => "Keypad" + code[6..],
            _ => code
        };
        target = target.Replace("_", "").Replace("-", "");
        foreach (var name in KeyNames)
        {
            if (string.Equals(name.Replace("_", ""), target, StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse(name, out key);
            if (target.Length == 1 && char.IsDigit(target[0]) &&
                string.Equals(name, "D" + target, StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse(name, out key);
        }
        return false;
    }

    private static bool TryModifier(string? code, out HMKeyModifiers modifier)
    {
        modifier = HMKeyModifiers.None;
        if (string.IsNullOrEmpty(code)) return false;
        var name = code switch
        {
            "ShiftLeft" => "LeftShift",
            "ShiftRight" => "RightShift",
            "ControlLeft" => "LeftControl",
            "ControlRight" => "RightControl",
            "AltLeft" => "LeftAlt",
            "AltRight" => "RightAlt",
            "MetaLeft" => "LeftGui",
            "MetaRight" => "RightGui",
            _ => code
        };
        foreach (var candidate in Enum.GetNames<HMKeyModifiers>())
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse(candidate, out modifier);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _operational = false;
            try { _gamepad?.Dispose(); } catch { }
            try { _mouse?.Dispose(); } catch { }
            try { _keyboard?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            _mouse = null; _keyboard = null; _gamepad = null; _context = null;
        }
    }

    public Task ShutdownAsync() { Dispose(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
