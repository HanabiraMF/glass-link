using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassLink.Capture;
using GlassLink.Encoding;
using GlassLink.Protocol;
using Microsoft.Extensions.Logging;

namespace GlassLink.Streaming;

public enum StreamState
{
    Idle,
    Starting,
    Streaming,
    Recovering,
    Stopping,
    Error
}

public sealed record StreamStateEvent(StreamState State, string? Error = null);

/// <summary>
/// Runs DXGI Desktop Duplication -> D3D11 Video Processor (BGRA->NV12) ->
/// Media Foundation hardware H.264 on a dedicated high-priority thread.
/// The GUI/WebSocket threads never touch GPU resources.
/// </summary>
public sealed class StreamSession : IAsyncDisposable
{
    private readonly ILogger<StreamSession> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _outputIndex;
    private volatile int _fps;
    private volatile int _bitrate;
    private readonly int _gop;
    private readonly bool _allowSoftwareEncoder;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private TaskCompletionSource? _stopped;
    private MediaFoundationEncoder? _encoder;
    private volatile bool _keyframeRequested;
    private StreamState _state = StreamState.Idle;
    private string? _lastError;
    private VideoStreamConfig? _videoConfig;
    private byte[]? _lastKeyframe;
    private StreamStatsDto _stats = new(0, 0, 0, 0);
    private bool _disposed;

    public StreamSession(
        ILoggerFactory loggerFactory,
        int outputIndex,
        int fps,
        int bitrate,
        int gop,
        bool allowSoftwareEncoder)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamSession>();
        _outputIndex = outputIndex;
        _fps = Math.Clamp(fps, 15, 240);
        _bitrate = Math.Max(1_000_000, bitrate);
        _gop = gop > 0 ? gop : Math.Clamp(_fps * 2, 30, 600);
        _allowSoftwareEncoder = allowSoftwareEncoder;
    }

    public event Action<StreamStateEvent>? StateChanged;
    public event Action<VideoStreamConfig>? VideoConfigChanged;
    public event Action<byte[]>? VideoPacketReady;
    public event Action<StreamStatsDto>? StatsChanged;
    public event Action<CursorSnapshot>? CursorChanged;

    public StreamState State => _state;
    public string? LastError => _lastError;
    public VideoStreamConfig? VideoConfig => _videoConfig;
    public byte[]? LastKeyframe => _lastKeyframe;
    public StreamStatsDto Stats => _stats;
    public CursorSnapshot Cursor { get; private set; }
    public bool IsStreaming => _state is StreamState.Starting or StreamState.Streaming or StreamState.Recovering;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;

            _cts = new CancellationTokenSource();
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = _cts.Token;
            var stopped = _stopped;
            _thread = new Thread(() => Run(token, stopped))
            {
                IsBackground = true,
                Name = "GlassLink-Capture-Encode",
                Priority = ThreadPriority.Highest
            };
            _thread.Start();
        }
    }

    public async Task StopAsync()
    {
        Thread? thread;
        TaskCompletionSource? stopped;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            thread = _thread;
            stopped = _stopped;
            cts = _cts;
            if (thread is null || stopped is null)
                return;
            SetState(StreamState.Stopping);
            try { cts?.Cancel(); } catch { }
        }

        if (stopped.Task.Wait(TimeSpan.FromSeconds(6)))
        {
            try { thread.Join(TimeSpan.FromSeconds(1)); } catch { }
            return;
        }

        _logger.LogWarning("Stream thread did not stop within timeout.");
        await Task.CompletedTask;
    }

    public void RequestKeyframe()
    {
        _keyframeRequested = true;
        try { _encoder?.RequestKeyframe(); } catch { }
    }

    /// <summary>
    /// Applies quality controls while the stream keeps running. FPS is enforced by
    /// frame skipping in the capture thread (never by Thread.Sleep); bitrate is
    /// handed to the Media Foundation encoder as a dynamic CodecAPI change.
    /// </summary>
    public VideoStreamConfig? ApplyVideoSettings(int fps, int bitrate)
    {
        var safeFps = Math.Clamp(fps, 15, 240);
        var safeBitrate = Math.Clamp(bitrate, 1_000_000, 80_000_000);
        _fps = safeFps;
        _bitrate = safeBitrate;

        MediaFoundationEncoder? encoder;
        lock (_sync) encoder = _encoder;
        try { encoder?.UpdateBitrate(safeBitrate); } catch { }

        VideoStreamConfig? updated;
        lock (_sync)
        {
            if (_videoConfig is null) return null;
            updated = _videoConfig with { Fps = safeFps, Bitrate = safeBitrate };
            _videoConfig = updated;
        }
        VideoConfigChanged?.Invoke(updated);
        _logger.LogInformation("Video settings updated: fps={Fps}, bitrate={Bitrate}.", safeFps, safeBitrate);
        return updated;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private void Run(CancellationToken cancellationToken, TaskCompletionSource stopped)
    {
        // Media Foundation hardware encoders are COM objects and their work-queue
        // threads require an initialized COM apartment (MTA) on the calling thread.
        var comResult = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        var comInitialized = comResult >= 0;
        var clock = Stopwatch.StartNew();
        var lastStatsAt = 0L;
        var framesSinceStats = 0L;
        var bytesSinceStats = 0L;
        var totalFrames = 0L;
        var totalBytes = 0L;
        var lastCursorPollAt = -1000.0;

        try
        {
            SetState(StreamState.Starting);
            while (!cancellationToken.IsCancellationRequested)
            {
                DxgiScreenCapturer? capturer = null;
                GpuColorConverter? converter = null;
                MediaFoundationEncoder? encoder = null;

                try
                {
                    SetState(StreamState.Starting);
                    capturer = DxgiScreenCapturer.Create(_outputIndex);
                    var initialFps = _fps;
                    var initialBitrate = _bitrate;
                    converter = GpuColorConverter.Create(capturer.Device, capturer.Context, capturer.Bounds, initialFps);

                    var encoderOptions = VideoEncoderOptions.Default(
                        capturer.Info.Bounds.Width,
                        capturer.Info.Bounds.Height,
                        initialFps,
                        initialBitrate) with
                    {
                        Gop = _gop,
                        AllowSoftwareFallback = _allowSoftwareEncoder
                    };

                    encoder = MediaFoundationEncoder.Create(_loggerFactory.CreateLogger<MediaFoundationEncoder>(), encoderOptions, capturer.Device);
                    lock (_sync) _encoder = encoder;
                    RequestKeyframe();

                    var config = new VideoStreamConfig(
                        encoder.CodecString,
                        capturer.Info.Bounds.Width,
                        capturer.Info.Bounds.Height,
                        initialFps,
                        initialBitrate,
                        encoder.EncoderName,
                        encoder.HardwareAccelerated);

                    lock (_sync) _videoConfig = config;
                    _lastKeyframe = null;
                    VideoConfigChanged?.Invoke(config);
                    SetState(StreamState.Streaming);
                    _logger.LogInformation("Streaming at {Width}x{Height}@{Fps}fps, {Mbps} Mbps, encoder={Encoder} ({Kind}).",
                        config.Width, config.Height, config.Fps, config.Bitrate / 1_000_000.0,
                        config.EncoderName, config.HardwareAccelerated ? "hardware" : "software");

                    var lastEncodedFrameAtMs = double.NegativeInfinity;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var frameStartMs = clock.Elapsed.TotalMilliseconds;
                        if (frameStartMs - lastCursorPollAt >= 33)
                        {
                            lastCursorPollAt = frameStartMs;
                            var cursor = CursorTracker.Get(capturer.Bounds);
                            if (cursor != Cursor)
                            {
                                Cursor = cursor;
                                CursorChanged?.Invoke(cursor);
                            }
                        }

                        if (_keyframeRequested)
                        {
                            _keyframeRequested = false;
                            encoder.RequestKeyframe();
                        }

                        // AcquireNextFrame internally waits for a desktop update.
                        // A long timeout makes a static desktop effectively zero-CPU.
                        if (!capturer.TryCaptureFrame(500, out var bgraTexture) || bgraTexture is null)
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            continue;
                        }

                        // Optional FPS target is enforced by dropping whole frames,
                        // never by sleeping the capture/encode thread. This avoids the
                        // old +processing-time lag and lets the newest frame win.
                        var desiredFps = Math.Clamp(_fps, 15, 240);
                        var minFrameIntervalMs = 1000.0 / desiredFps * 0.90;
                        if (frameStartMs - lastEncodedFrameAtMs < minFrameIntervalMs)
                            continue;

                        lastEncodedFrameAtMs = frameStartMs;
                        var nv12 = converter.Convert(bgraTexture);
                        var timestampUs = clock.Elapsed.Ticks / 10;
                        var encoded = encoder.Encode(nv12, timestampUs);

                        VideoStreamConfig? currentConfig;
                        lock (_sync) currentConfig = _videoConfig;
                        if (currentConfig is not null &&
                            !string.Equals(currentConfig.Codec, encoder.CodecString, StringComparison.Ordinal))
                        {
                            currentConfig = currentConfig with { Codec = encoder.CodecString };
                            lock (_sync) _videoConfig = currentConfig;
                            VideoConfigChanged?.Invoke(currentConfig);
                        }

                        foreach (var frame in encoded)
                        {
                            var packet = VideoPacket.Build(frame);
                            if (frame.IsKeyframe) _lastKeyframe = packet;
                            VideoPacketReady?.Invoke(packet);

                            framesSinceStats++;
                            totalFrames++;
                            bytesSinceStats += packet.Length;
                            totalBytes += packet.Length;
                        }

                        if (clock.ElapsedMilliseconds - lastStatsAt >= 1000)
                        {
                            var elapsed = Math.Max(0.001, (clock.ElapsedMilliseconds - lastStatsAt) / 1000.0);
                            var stats = new StreamStatsDto(
                                framesSinceStats / elapsed,
                                bytesSinceStats * 8.0 / elapsed / 1_000_000.0,
                                totalFrames,
                                totalBytes);
                            _stats = stats;
                            StatsChanged?.Invoke(stats);
                            lastStatsAt = clock.ElapsedMilliseconds;
                            framesSinceStats = 0;
                            bytesSinceStats = 0;
                        }
                    }
                }
                catch (CaptureReinitializeRequiredException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Capture pipeline requires reinitialization.");
                    SetState(StreamState.Recovering, ex.Message);
                    lock (_sync) _encoder = null;
                    encoder?.Dispose();
                    converter?.Dispose();
                    capturer?.Dispose();
                    Thread.Sleep(800);
                    continue;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Streaming pipeline failed.");
                    _lastError = ex.Message;
                    SetState(StreamState.Error, ex.Message);
                    lock (_sync) _encoder = null;
                    encoder?.Dispose();
                    converter?.Dispose();
                    capturer?.Dispose();
                    return;
                }
                finally
                {
                    lock (_sync) _encoder = null;
                    encoder?.Dispose();
                    converter?.Dispose();
                    capturer?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "Unhandled streaming thread error.");
            SetState(StreamState.Error, ex.Message);
        }
        finally
        {
            _logger.LogInformation("Stream thread finished after {Frames} frames / {MB:F1} MiB.", totalFrames, totalBytes / 1048576.0);
            Cursor = new CursorSnapshot(false, 0, 0);
            if (_state != StreamState.Error)
            {
                _lastError = null;
                SetState(StreamState.Idle);
            }
            lock (_sync)
            {
                _thread = null;
                _cts?.Dispose();
                _cts = null;
            }
            if (comInitialized) CoUninitialize();
            stopped.TrySetResult();
        }
    }

    private void SetState(StreamState state, string? error = null)
    {
        _state = state;
        if (error is not null) _lastError = error;
        StateChanged?.Invoke(new StreamStateEvent(state, error));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}
