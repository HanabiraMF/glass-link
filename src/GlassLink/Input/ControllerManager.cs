using GlassLink.Input.Hardware;
using GlassLink.Input.Software;
using GlassLink.Protocol;
using Microsoft.Extensions.Logging;

namespace GlassLink.Input;

/// <summary>
/// Runtime controller-layer selection with explicit fallback semantics.
/// Video capture/encoding is completely independent from this manager, so a layer
/// switch never restarts or interrupts the video stream.
/// </summary>
public sealed class ControllerManager : IAsyncDisposable
{
    private readonly ILogger<ControllerManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly object _injectLock = new();
    private IInputController? _active;
    private SoftwareInputController? _touchFallback;
    private SoftwareInputController? _keyboardMouseFallback;
    private SoftwareInputController? _absoluteMouseFallback;
    private CaptureBounds _bounds;
    private string _requestedLayer;
    private string _activeLayer = ControllerLayerIds.Software;
    private string _activeDisplayName = "-";
    private bool _hardwareAvailable;
    private string? _fallbackReason;
    private IReadOnlyList<string> _diagnostics = Array.Empty<string>();
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public ControllerManager(ILoggerFactory loggerFactory, string requestedLayer)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ControllerManager>();
        _requestedLayer = ControllerLayerIds.IsValid(requestedLayer) ? requestedLayer : ControllerLayerIds.Auto;
        _bounds = ScreenGeometry.PrimaryBounds();
    }

    public event Action<ControllerRuntimeStatus>? StatusChanged;

    public string RequestedLayer
    {
        get { lock (_injectLock) return _requestedLayer; }
    }

    public string ActiveLayer
    {
        get { lock (_injectLock) return _activeLayer; }
    }

    public ControllerRuntimeStatus Status
    {
        get
        {
            lock (_injectLock)
            {
                return new ControllerRuntimeStatus(
                    _requestedLayer,
                    _activeLayer,
                    _hardwareAvailable,
                    _activeDisplayName,
                    _fallbackReason,
                    _diagnostics,
                    _changedAt);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SwitchLayerAsync(_requestedLayer, cancellationToken, initial: true);
    }

    public async Task<ControllerRuntimeStatus> SetLayerAsync(string requestedLayer, CancellationToken cancellationToken = default)
    {
        if (!ControllerLayerIds.IsValid(requestedLayer))
            throw new ArgumentException($"Unknown controller layer '{requestedLayer}'.", nameof(requestedLayer));

        await SwitchLayerAsync(requestedLayer, cancellationToken, initial: false);
        return Status;
    }

    private async Task SwitchLayerAsync(string requestedLayer, CancellationToken cancellationToken, bool initial)
    {
        await _switchLock.WaitAsync(cancellationToken);
        try
        {
            var diagnostics = new List<string>();
            var previous = _active;
            IInputController? candidate = null;
            var activeLayer = ControllerLayerIds.Software;
            var activeName = "软件注入层（SendInput + Touch Injection）";
            var hardwareAvailable = false;
            string? fallback = null;
            SoftwareInputController? newTouchFallback = null;
            SoftwareInputController? newKeyboardMouseFallback = null;
            SoftwareInputController? newAbsoluteMouseFallback = null;

            var tryHardware = requestedLayer is ControllerLayerIds.Hardware or ControllerLayerIds.Auto;

            if (tryHardware)
            {
                var hardware = new HidMaestroInputController(_loggerFactory.CreateLogger<HidMaestroInputController>());
                var hardwareActivation = await hardware.ActivateAsync(_bounds, cancellationToken);
                diagnostics.AddRange(hardwareActivation.Diagnostics ?? Array.Empty<string>());

                if (hardwareActivation.Success)
                {
                    candidate = hardware;
                    activeLayer = ControllerLayerIds.Hardware;
                    activeName = hardware.DisplayName;
                    hardwareAvailable = true;

                    // HIDMaestro's mouse is relative. Desktop pages send absolute
                    // coordinates and need the software SendInput absolute path so
                    // the virtual cursor cannot drift away from the browser pointer.
                    var absoluteMouse = new SoftwareInputController(_loggerFactory.CreateLogger<SoftwareInputController>());
                    var absoluteActivation = await absoluteMouse.ActivateAsync(_bounds, cancellationToken);
                    if (absoluteActivation.Success)
                    {
                        newAbsoluteMouseFallback = absoluteMouse;
                        diagnostics.Add("桌面端绝对鼠标使用 SendInput 绝对定位；键盘/移动端走 HIDMaestro。");
                    }
                    else
                    {
                        await absoluteMouse.DisposeAsync();
                    }
                }
                else
                {
                    fallback = hardwareActivation.Reason ?? "驱动注入不可用。";
                    diagnostics.Add("驱动注入不可用，回退到系统注入。");
                    await hardware.DisposeAsync();
                }
            }
            else
            {
                diagnostics.Add("用户显式选择系统注入。");
            }

            if (candidate is null)
            {
                // Explicit hardware selection still falls back so the user keeps
                // a working mouse/keyboard/touch path and sees a friendly warning.
                var software = new SoftwareInputController(_loggerFactory.CreateLogger<SoftwareInputController>());
                var softwareActivation = await software.ActivateAsync(_bounds, cancellationToken);
                diagnostics.AddRange(softwareActivation.Diagnostics ?? Array.Empty<string>());
                if (!softwareActivation.Success)
                {
                    await software.DisposeAsync();
                    throw new InvalidOperationException("Both input layers failed to initialize.");
                }
                candidate = software;
                activeLayer = ControllerLayerIds.Software;
                activeName = software.DisplayName;
                if (requestedLayer == ControllerLayerIds.Hardware && fallback is null)
                    fallback = "虚拟硬件层不可用。";
            }

            IInputController? old;
            SoftwareInputController? oldTouchFallback;
            SoftwareInputController? oldKeyboardMouseFallback;
            SoftwareInputController? oldAbsoluteMouseFallback;
            lock (_injectLock)
            {
                old = _active;
                oldTouchFallback = _touchFallback;
                oldKeyboardMouseFallback = _keyboardMouseFallback;
                oldAbsoluteMouseFallback = _absoluteMouseFallback;
                _touchFallback = newTouchFallback;
                _keyboardMouseFallback = newKeyboardMouseFallback;
                _absoluteMouseFallback = newAbsoluteMouseFallback;
                _active = candidate;
                _requestedLayer = requestedLayer;
                _activeLayer = activeLayer;
                _activeDisplayName = activeName;
                _hardwareAvailable = hardwareAvailable || _activeLayer == ControllerLayerIds.Hardware;
                _fallbackReason = fallback;
                _diagnostics = diagnostics;
                _changedAt = DateTimeOffset.UtcNow;
            }

            if (old is not null && !ReferenceEquals(old, candidate))
            {
                try { await old.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose previous input layer."); }
            }
            if (oldTouchFallback is not null && !ReferenceEquals(oldTouchFallback, newTouchFallback))
            {
                try { await oldTouchFallback.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose touch fallback."); }
            }
            if (oldKeyboardMouseFallback is not null && !ReferenceEquals(oldKeyboardMouseFallback, newKeyboardMouseFallback))
            {
                try { await oldKeyboardMouseFallback.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose keyboard/mouse fallback."); }
            }
            if (oldAbsoluteMouseFallback is not null && !ReferenceEquals(oldAbsoluteMouseFallback, newAbsoluteMouseFallback))
            {
                try { await oldAbsoluteMouseFallback.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose absolute mouse fallback."); }
            }

            _logger.LogInformation("Input layer switched: requested={Requested}, active={Active}, fallback={Fallback}",
                requestedLayer, activeLayer, fallback ?? "none");

            if (!initial && fallback is not null)
            {
                _logger.LogWarning("Input layer fallback: {Fallback}", fallback);
            }

            StatusChanged?.Invoke(Status);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public void UpdateBounds(CaptureBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        lock (_injectLock)
        {
            _bounds = bounds;
            _active?.UpdateBounds(bounds);
        }
    }

    public bool Inject(InputEvent inputEvent)
    {
        // Keep the injection lock while dispatching. Layer switching takes the
        // same lock to swap the active controller, so a layer cannot be disposed
        // while one of its Inject calls is still running.
        lock (_injectLock)
        {
            if (_active is null) return false;
            try
            {
                var isMouse = inputEvent.Type is InputTypeNames.MouseMove or InputTypeNames.MouseButton or InputTypeNames.MouseWheel;
                if (isMouse && string.Equals(inputEvent.Source, "desktop", StringComparison.OrdinalIgnoreCase) &&
                    _absoluteMouseFallback is not null)
                    return _absoluteMouseFallback.Inject(inputEvent);

                if (string.Equals(inputEvent.Type, InputTypeNames.Touch, StringComparison.OrdinalIgnoreCase))
                {
                    if (_touchFallback is not null) return _touchFallback.Inject(inputEvent);
                }
                else if (_keyboardMouseFallback is not null)
                {
                    return _keyboardMouseFallback.Inject(inputEvent);
                }
                return _active.Inject(inputEvent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Input injection failed.");
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _switchLock.WaitAsync();
        try
        {
            IInputController? active;
            SoftwareInputController? touchFallback;
            SoftwareInputController? keyboardMouseFallback;
            SoftwareInputController? absoluteMouseFallback;
            lock (_injectLock)
            {
                active = _active;
                touchFallback = _touchFallback;
                keyboardMouseFallback = _keyboardMouseFallback;
                absoluteMouseFallback = _absoluteMouseFallback;
                _active = null;
                _touchFallback = null;
                _keyboardMouseFallback = null;
                _absoluteMouseFallback = null;
            }
            if (active is not null) await active.DisposeAsync();
            if (touchFallback is not null) await touchFallback.DisposeAsync();
            if (keyboardMouseFallback is not null) await keyboardMouseFallback.DisposeAsync();
            if (absoluteMouseFallback is not null) await absoluteMouseFallback.DisposeAsync();
        }
        finally
        {
            _switchLock.Release();
            _switchLock.Dispose();
        }
    }
}
