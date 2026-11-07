using System.Text.Json.Serialization;

namespace GlassLink.Protocol;

/// <summary>
/// The single, transport-independent input model shared by both injection layers.
/// Frontends only speak this model. A controller layer is solely responsible for
/// translating these events into SendInput / Touch Injection / Interception / vmulti calls.
/// </summary>
public sealed class InputEvent
{
    /// <summary>keyboard | mouseMove | mouseButton | mouseWheel | touch | text</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>down | up | move | cancel (meaning depends on Type)</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>KeyboardEvent.code, for example KeyW, Space, ShiftLeft.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>KeyboardEvent.key. Used only as a fallback for unusual layouts.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("virtualKey")]
    public int? VirtualKey { get; set; }

    [JsonPropertyName("scanCode")]
    public int? ScanCode { get; set; }

    [JsonPropertyName("extended")]
    public bool? Extended { get; set; }

    /// <summary>left | right | middle | x1 | x2</summary>
    [JsonPropertyName("button")]
    public string? Button { get; set; }

    /// <summary>relative | absolute</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("dx")]
    public double? Dx { get; set; }

    [JsonPropertyName("dy")]
    public double? Dy { get; set; }

    /// <summary>Normalized absolute X in [0,1] relative to the captured output.</summary>
    [JsonPropertyName("x")]
    public double? X { get; set; }

    /// <summary>Normalized absolute Y in [0,1] relative to the captured output.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("wheelDelta")]
    public int? WheelDelta { get; set; }

    [JsonPropertyName("wheelDeltaX")]
    public int? WheelDeltaX { get; set; }

    /// <summary>Batch of touch contacts for a single frame.</summary>
    [JsonPropertyName("touches")]
    public List<TouchContact>? Touches { get; set; }

    /// <summary>Optional provenance: desktop | mobile-normal | mobile-game. Not used by injection.</summary>
    [JsonPropertyName("gamepad")]
    public GamepadInput? Gamepad { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    public InputEvent Clone() => new()
    {
        Type = Type,
        Action = Action,
        Code = Code,
        Key = Key,
        VirtualKey = VirtualKey,
        ScanCode = ScanCode,
        Extended = Extended,
        Button = Button,
        Mode = Mode,
        Dx = Dx,
        Dy = Dy,
        X = X,
        Y = Y,
        WheelDelta = WheelDelta,
        WheelDeltaX = WheelDeltaX,
        Touches = Touches?.Select(t => t.Clone()).ToList(),
        Gamepad = Gamepad?.Clone(),
        Source = Source
    };
}

public sealed class GamepadInput
{
    [JsonPropertyName("buttons")] public List<string> Buttons { get; set; } = new();
    [JsonPropertyName("leftX")] public double LeftX { get; set; }
    [JsonPropertyName("leftY")] public double LeftY { get; set; }
    [JsonPropertyName("rightX")] public double RightX { get; set; }
    [JsonPropertyName("rightY")] public double RightY { get; set; }
    [JsonPropertyName("leftTrigger")] public double LeftTrigger { get; set; }
    [JsonPropertyName("rightTrigger")] public double RightTrigger { get; set; }

    public GamepadInput Clone() => new()
    {
        Buttons = Buttons.ToList(), LeftX = LeftX, LeftY = LeftY,
        RightX = RightX, RightY = RightY,
        LeftTrigger = LeftTrigger, RightTrigger = RightTrigger
    };
}

public sealed class TouchContact
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>down | move | up | cancel</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "move";

    [JsonPropertyName("pressure")]
    public double? Pressure { get; set; }

    public TouchContact Clone() => new()
    {
        Id = Id,
        X = X,
        Y = Y,
        Phase = Phase,
        Pressure = Pressure
    };
}

/// <summary>A rectangular capture area in virtual-desktop pixels.</summary>
public readonly record struct CaptureBounds(int X, int Y, int Width, int Height)
{
    public double NormalizedXToPixel(double normalizedX) => X + Math.Clamp(normalizedX, 0, 1) * Math.Max(1, Width - 1);
    public double NormalizedYToPixel(double normalizedY) => Y + Math.Clamp(normalizedY, 0, 1) * Math.Max(1, Height - 1);
}

public static class InputTypeNames
{
    public const string Keyboard = "keyboard";
    public const string MouseMove = "mouseMove";
    public const string MouseButton = "mouseButton";
    public const string MouseWheel = "mouseWheel";
    public const string Touch = "touch";
    public const string Text = "text";
    public const string Gamepad = "gamepad";
}

public static class ControllerLayerIds
{
    public const string Auto = "auto";
    public const string Software = "software";
    public const string Hardware = "hardware";

    public static bool IsValid(string? value) =>
        value is Auto or Software or Hardware;
}
