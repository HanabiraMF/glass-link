using GlassLink.Protocol;

namespace GlassLink.Input;

public sealed record ControllerActivation(
    bool Success,
    string ActiveLayer,
    string RequestedLayer,
    string? Reason = null,
    IReadOnlyList<string>? Diagnostics = null);

public sealed record ControllerRuntimeStatus(
    string RequestedLayer,
    string ActiveLayer,
    bool IsHardwareAvailable,
    string ActiveDisplayName,
    string? FallbackReason,
    IReadOnlyList<string> Diagnostics,
    DateTimeOffset ChangedAtUtc);

/// <summary>
/// Implemented by the software and virtual-hardware injection layers.
/// Both consume exactly the same unified InputEvent model.
/// </summary>
public interface IInputController : IAsyncDisposable
{
    string LayerId { get; }
    string DisplayName { get; }
    CaptureBounds Bounds { get; }
    bool IsOperational { get; }

    Task<ControllerActivation> ActivateAsync(CaptureBounds bounds, CancellationToken cancellationToken);
    void UpdateBounds(CaptureBounds bounds);
    bool Inject(InputEvent inputEvent);
    Task ShutdownAsync();
}
