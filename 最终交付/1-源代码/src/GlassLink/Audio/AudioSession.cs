using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace GlassLink.Audio;

/// <summary>
/// Captures the Windows default playback device via WASAPI loopback and emits
/// small PCM16 packets. PCM keeps the browser side dependency-free; a 48 kHz
/// stereo stream is ~1.5 Mbps, acceptable on the same LAN as the video stream.
/// </summary>
public sealed class AudioSession : IDisposable
{
    public const int HeaderSize = 16;
    public const int MaxQueuedPackets = 8;

    private readonly ILogger<AudioSession> _logger;
    private readonly object _sync = new();
    private WasapiLoopbackCapture? _capture;
    private Action<byte[]>? _onPacket;
    private int _sequence;
    private bool _disposed;

    public AudioSession(ILogger<AudioSession> logger) => _logger = logger;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public bool Start(Action<byte[]> onPacket)
    {
        lock (_sync)
        {
            if (IsRunning) return true;
            if (_disposed) return false;

            try
            {
                _onPacket = onPacket;
                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                IsRunning = true;
                LastError = null;
                _logger.LogInformation("Audio loopback started: {Format}.", _capture.WaveFormat);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogWarning(ex, "Audio loopback could not start; video continues without audio.");
                StopLocked();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    private void StopLocked()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _onPacket = null;
        IsRunning = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var capture = _capture;
        if (capture is null) return;

        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var sampleRate = Math.Max(8000, format.SampleRate);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0) return;

        var samples = e.BytesRecorded / bytesPerSample;
        var pcm = new byte[samples * 2];
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.ToSingle(e.Buffer, i * 4);
                var s = (short)Math.Clamp((int)Math.Round(value * 32767f), short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        else if (bytesPerSample == 2)
        {
            Buffer.BlockCopy(e.Buffer, 0, pcm, 0, Math.Min(pcm.Length, e.BytesRecorded));
        }
        else
        {
            return; // unsupported capture format; video is unaffected
        }

        var packet = new byte[HeaderSize + pcm.Length];
        packet[0] = (byte)'G'; packet[1] = (byte)'L'; packet[2] = (byte)'A'; packet[3] = (byte)'1';
        BitConverter.TryWriteBytes(packet.AsSpan(4, 4), (uint)sampleRate);
        BitConverter.TryWriteBytes(packet.AsSpan(8, 2), (ushort)channels);
        BitConverter.TryWriteBytes(packet.AsSpan(10, 2), (ushort)16);
        BitConverter.TryWriteBytes(packet.AsSpan(12, 4), (uint)Interlocked.Increment(ref _sequence));
        Buffer.BlockCopy(pcm, 0, packet, HeaderSize, pcm.Length);
        _onPacket?.Invoke(packet);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogWarning(e.Exception, "Audio loopback stopped unexpectedly.");
        IsRunning = false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopLocked();
        }
    }
}
