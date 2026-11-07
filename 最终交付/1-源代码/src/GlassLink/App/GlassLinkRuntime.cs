using System.Net.WebSockets;
using System.Text.Json;
using GlassLink.Capture;
using GlassLink.Input;
using GlassLink.Audio;
using GlassLink.Protocol;
using GlassLink.Security;
using GlassLink.Streaming;
using Microsoft.Extensions.Logging;

namespace GlassLink.App;

public sealed class GlassLinkRuntime : IAsyncDisposable
{
    private readonly AppOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GlassLinkRuntime> _logger;
    private readonly ControllerManager _controller;
    private readonly AudioSession _audio;
    private readonly StreamSession _stream;
    private readonly WebSocketHub _hub;
    private readonly LocalCertificate? _certificate;
    private readonly string _httpUrl;
    private readonly string? _httpsUrl;
    private readonly string _serverVersion = typeof(GlassLinkRuntime).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private CursorSnapshot _lastCursor;
    private bool _disposed;

    public GlassLinkRuntime(AppOptions options, ILoggerFactory loggerFactory, LocalCertificate? certificate)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GlassLinkRuntime>();
        _certificate = certificate;
        var lan = AppOptions.GetPrimaryLanAddress();
        _httpUrl = $"http://{lan}:{options.HttpPort}";
        _httpsUrl = certificate is not null ? $"https://{lan}:{options.HttpsPort}" : null;

        _controller = new ControllerManager(loggerFactory, options.Controller);
        _audio = new AudioSession(loggerFactory.CreateLogger<AudioSession>());
        _stream = new StreamSession(
            loggerFactory,
            options.OutputIndex,
            options.Fps,
            options.Bitrate,
            options.Gop,
            options.AllowSoftwareEncoder);

        _hub = new WebSocketHub(
            loggerFactory.CreateLogger<WebSocketHub>(),
            OnClientConnectedAsync,
            OnClientMessageAsync,
            OnClientDisconnectedAsync);

        _stream.StateChanged += OnStreamStateChanged;
        _stream.VideoConfigChanged += OnVideoConfigChanged;
        _stream.VideoPacketReady += OnVideoPacketReady;
        _stream.StatsChanged += OnStatsChanged;
        _stream.CursorChanged += OnCursorChanged;
        _controller.StatusChanged += OnControllerStatusChanged;
    }

    public WebSocketHub Hub => _hub;
    public AppOptions Options => _options;
    public LocalCertificate? Certificate => _certificate;
    public StreamSession Stream => _stream;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _controller.InitializeAsync(cancellationToken);
        var status = _controller.Status;
        _logger.LogInformation("Input controller: requested={Requested}, active={Active}, hardwareAvailable={Hardware}.",
            status.RequestedLayer, status.ActiveLayer, status.IsHardwareAvailable);
        if (status.FallbackReason is not null)
            _logger.LogWarning("Input layer fallback: {Reason}", status.FallbackReason);
    }

    public bool IsAuthorized(HttpContext context)
    {
        if (string.IsNullOrEmpty(_options.Token)) return true;
        var supplied = context.Request.Query["token"].ToString();
        if (string.IsNullOrEmpty(supplied))
            supplied = context.Request.Headers["X-GlassLink-Token"].ToString();
        return string.Equals(supplied, _options.Token, StringComparison.Ordinal);
    }

    public ServerStatusDto GetStatus()
    {
        var controller = _controller.Status;
        var config = _stream.VideoConfig;
        return new ServerStatusDto(
            Streaming: _stream.IsStreaming,
            StreamState: _stream.State.ToString().ToLowerInvariant(),
            StreamError: _stream.LastError,
            Clients: _hub.Count,
            SecureContextAvailable: _certificate is not null,
            HttpUrl: _httpUrl,
            HttpsUrl: _httpsUrl ?? "",
            Controller: ToDto(controller),
            Video: config is null
                ? null
                : new VideoInfoDto(config.Codec, config.Width, config.Height, config.Fps, config.Bitrate,
                    config.EncoderName, config.HardwareAccelerated, config.Format),
            Stats: _stream.Stats);
    }

    public async Task StartStreamAsync()
    {
        if (_stream.IsStreaming)
            return;
        _logger.LogInformation("Stream start requested.");
        _stream.Start();
        _audio.Start(_hub.BroadcastAudio);
        await Task.CompletedTask;
    }

    public async Task StopStreamAsync()
    {
        if (!_stream.IsStreaming)
            return;
        _logger.LogInformation("Stream stop requested.");
        _audio.Stop();
        await _stream.StopAsync();
    }

    public async Task<ControllerRuntimeStatus> SetControllerAsync(string layer, CancellationToken cancellationToken = default)
    {
        var status = await _controller.SetLayerAsync(layer, cancellationToken);
        _logger.LogInformation("Controller layer changed by UI: active={Active}, fallback={Fallback}.",
            status.ActiveLayer, status.FallbackReason ?? "none");
        return status;
    }

    public void RequestKeyframe() => _stream.RequestKeyframe();

    private async Task OnClientConnectedAsync(ClientConnection client)
    {
        var hello = new
        {
            type = "hello",
            clientId = client.Id,
            role = client.Role,
            serverVersion = _serverVersion,
            status = GetStatus()
        };
        _hub.SendText(client, ProtocolJson.Serialize(hello));
        _hub.SendText(client, ProtocolJson.Serialize(new
        {
            type = "cursor",
            cursor = new { visible = _lastCursor.Visible, x = _lastCursor.X, y = _lastCursor.Y }
        }));
        SendVideoSnapshot(client);
        _stream.RequestKeyframe();
        await Task.CompletedTask;
    }

    private void SendVideoSnapshot(ClientConnection client)
    {
        var config = _stream.VideoConfig;
        if (config is null) return;

        var video = new VideoInfoDto(config.Codec, config.Width, config.Height, config.Fps, config.Bitrate,
            config.EncoderName, config.HardwareAccelerated, config.Format);
        _hub.SendText(client, ProtocolJson.Serialize(new { type = "videoConfig", video }));
        var keyframe = _stream.LastKeyframe;
        if (keyframe is not null)
            _hub.SendVideo(client, keyframe);
    }

    private async Task OnClientDisconnectedAsync(ClientConnection client)
    {
        BroadcastStatus();
        await Task.CompletedTask;
    }

    private async Task OnClientMessageAsync(ClientConnection client, ClientMessage message)
    {
        switch (message.Type)
        {
            case "hello":
                if (message.Role is "desktop" or "mobile")
                    client.Role = message.Role;
                if (!string.IsNullOrWhiteSpace(message.ClientName))
                    client.Name = message.ClientName.Length > 80
                        ? message.ClientName[..80]
                        : message.ClientName;
                _hub.SendText(client, ProtocolJson.Serialize(new { type = "hello", clientId = client.Id, role = client.Role, serverVersion = _serverVersion, status = GetStatus() }));
                SendVideoSnapshot(client);
                break;

            case "stream.start":
                await StartStreamAsync();
                break;

            case "stream.stop":
                await StopStreamAsync();
                BroadcastStatus();
                break;

            case "controller.set":
                if (string.IsNullOrWhiteSpace(message.Layer) || !ControllerLayerIds.IsValid(message.Layer))
                {
                    _hub.SendError(client, "控制器层必须是 auto、software 或 hardware。");
                    break;
                }
                var controller = await SetControllerAsync(message.Layer);
                BroadcastControllerStatus();
                BroadcastStatus();
                break;

            case "input":
                if (message.Event is null)
                {
                    _hub.SendError(client, "input 消息缺少 event。");
                    break;
                }
                var input = SanitizeInput(message.Event, client.Role);
                if (input is not null)
                    _controller.Inject(input);
                break;

            case "ping":
                _hub.SendText(client, ProtocolJson.Serialize(new { type = "pong", t = message.T }));
                break;

            case "video.settings":
                if (message.Fps is null && message.Bitrate is null)
                {
                    _hub.SendError(client, "video.settings 需要 fps 或 bitrate。");
                    break;
                }
                var currentVideo = _stream.VideoConfig;
                _stream.ApplyVideoSettings(
                    message.Fps ?? currentVideo?.Fps ?? _options.Fps,
                    message.Bitrate ?? currentVideo?.Bitrate ?? _options.Bitrate);
                BroadcastStatus();
                break;

            case "keyframe":
                _stream.RequestKeyframe();
                break;

            case "status":
                _hub.SendText(client, ProtocolJson.Serialize(new { type = "status", status = GetStatus() }));
                break;

            default:
                _hub.SendError(client, $"不支持的消息类型：{message.Type}");
                break;
        }
    }

    private static InputEvent? SanitizeInput(InputEvent input, string role)
    {
        if (!string.Equals(input.Type, InputTypeNames.Keyboard, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.MouseMove, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.MouseButton, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.MouseWheel, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.Touch, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.Gamepad, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.Type, InputTypeNames.Text, StringComparison.OrdinalIgnoreCase))
            return null;

        var sanitized = input.Clone();
        sanitized.Source ??= role;

        if (sanitized.Type == InputTypeNames.Text)
        {
            if (sanitized.Key is { Length: > 1024 } text) sanitized.Key = text[..1024];
            if (string.IsNullOrEmpty(sanitized.Key)) return null;
        }

        if (sanitized.Dx is { } dx) sanitized.Dx = Math.Clamp(dx, -4096, 4096);
        if (sanitized.Dy is { } dy) sanitized.Dy = Math.Clamp(dy, -4096, 4096);
        if (sanitized.X is { } x) sanitized.X = Math.Clamp(x, 0, 1);
        if (sanitized.Y is { } y) sanitized.Y = Math.Clamp(y, 0, 1);
        if (sanitized.WheelDelta is { } wheel) sanitized.WheelDelta = Math.Clamp(wheel, -2400, 2400);
        if (sanitized.WheelDeltaX is { } wheelX) sanitized.WheelDeltaX = Math.Clamp(wheelX, -2400, 2400);

        if (sanitized.Touches is { } touches)
        {
            if (touches.Count > 20) touches = touches.Take(20).ToList();
            foreach (var touch in touches)
            {
                touch.X = Math.Clamp(touch.X, 0, 1);
                touch.Y = Math.Clamp(touch.Y, 0, 1);
                if (touch.Id < 0) touch.Id = 0;
                if (touch.Phase is not ("down" or "move" or "up" or "cancel")) touch.Phase = "move";
            }
            sanitized.Touches = touches;
        }

        if (sanitized.Action is not null)
            sanitized.Action = sanitized.Action.ToLowerInvariant();

        return sanitized;
    }

    private void OnCursorChanged(CursorSnapshot cursor)
    {
        _lastCursor = cursor;
        _hub.BroadcastText(ProtocolJson.Serialize(new
        {
            type = "cursor",
            cursor = new { visible = cursor.Visible, x = cursor.X, y = cursor.Y }
        }));
    }

    private void OnStreamStateChanged(StreamStateEvent state)
    {
        _hub.BroadcastText(ProtocolJson.Serialize(new { type = "streamState", state = state.State.ToString().ToLowerInvariant(), error = state.Error }));
        if (!_stream.IsStreaming)
        {
            _lastCursor = new CursorSnapshot(false, 0, 0);
            _hub.BroadcastText(ProtocolJson.Serialize(new { type = "cursor", cursor = new { visible = false, x = 0.0, y = 0.0 } }));
        }
        BroadcastStatus();
        if (state.State == StreamState.Error && !string.IsNullOrWhiteSpace(state.Error))
            _logger.LogError("Stream error: {Error}", state.Error);
    }

    private void OnVideoConfigChanged(Encoding.VideoStreamConfig config)
    {
        var video = new VideoInfoDto(config.Codec, config.Width, config.Height, config.Fps, config.Bitrate,
            config.EncoderName, config.HardwareAccelerated, config.Format);
        _hub.BroadcastText(ProtocolJson.Serialize(new { type = "videoConfig", video }));
        BroadcastStatus();
    }

    private void OnVideoPacketReady(byte[] packet) => _hub.BroadcastVideo(packet);

    private void OnStatsChanged(StreamStatsDto stats)
    {
        _hub.BroadcastText(ProtocolJson.Serialize(new { type = "videoStats", stats }));
    }

    private void OnControllerStatusChanged(ControllerRuntimeStatus status)
    {
        BroadcastControllerStatus();
        BroadcastStatus();
    }

    private void BroadcastControllerStatus()
    {
        _hub.BroadcastText(ProtocolJson.Serialize(new { type = "controllerStatus", controller = ToDto(_controller.Status) }));
    }

    private void BroadcastStatus()
    {
        _hub.BroadcastText(ProtocolJson.Serialize(new { type = "status", status = GetStatus() }));
    }

    private static ControllerDto ToDto(ControllerRuntimeStatus status) => new(
        status.RequestedLayer,
        status.ActiveLayer,
        status.ActiveDisplayName,
        status.IsHardwareAvailable,
        status.FallbackReason,
        status.Diagnostics,
        status.ChangedAtUtc);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.StateChanged -= OnStreamStateChanged;
        _stream.VideoConfigChanged -= OnVideoConfigChanged;
        _stream.VideoPacketReady -= OnVideoPacketReady;
        _stream.StatsChanged -= OnStatsChanged;
        _stream.CursorChanged -= OnCursorChanged;
        _controller.StatusChanged -= OnControllerStatusChanged;
        _audio.Dispose();
        await _stream.DisposeAsync();
        await _hub.DisposeAsync();
        await _controller.DisposeAsync();
    }
}
