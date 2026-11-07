using System.Collections.Concurrent;
using System.Net.WebSockets;
using GlassLink.Audio;

namespace GlassLink.Streaming;

/// <summary>
/// One browser WebSocket. The capture/encode thread never waits for network I/O:
/// it only deposits bytes into this object. Control messages are queued reliably;
/// video is a latest-frame-wins mailbox (one pending keyframe + one latest delta),
/// so a congested client can never build a growing latency backlog.
/// </summary>
public sealed class ClientConnection : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ConcurrentQueue<string> _control = new();
    private readonly ConcurrentQueue<byte[]> _audio = new();
    private readonly object _videoLock = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;
    private byte[]? _pendingKeyframe;
    private byte[]? _pendingDelta;
    private int _wakeSignaled;
    private int _closed;
    private long _droppedVideoFrames;

    public ClientConnection(string id, string role, WebSocket socket)
    {
        Id = id;
        Role = role;
        _socket = socket;
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string Role { get; set; }
    public string Name { get; set; } = "匿名客户端";
    public DateTimeOffset ConnectedAt { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public long DroppedVideoFrames => Interlocked.Read(ref _droppedVideoFrames);

    public void StartSendLoop()
    {
        _sendLoop ??= Task.Run(SendLoopAsync);
    }

    public void QueueControl(string json)
    {
        if (IsClosed) return;
        _control.Enqueue(json);
        Signal();
    }

    public void QueueVideo(byte[] packet)
    {
        if (IsClosed) return;

        // GLV1 flags: bit0 = keyframe.
        var isKeyframe = packet.Length > 4 && (packet[4] & 1) != 0;
        lock (_videoLock)
        {
            if (isKeyframe)
            {
                // Never drop a keyframe. It supersedes an older keyframe and any
                // pending delta that depended on the previous GOP.
                if (_pendingKeyframe is not null || _pendingDelta is not null)
                    Interlocked.Increment(ref _droppedVideoFrames);
                _pendingKeyframe = packet;
                _pendingDelta = null;
            }
            else
            {
                if (_pendingDelta is not null)
                    Interlocked.Increment(ref _droppedVideoFrames);
                _pendingDelta = packet;
            }
        }

        Signal();
    }

    public void QueueAudio(byte[] packet)
    {
        if (IsClosed) return;
        _audio.Enqueue(packet);
        while (_audio.Count > AudioSession.MaxQueuedPackets && _audio.TryDequeue(out _)) { }
        Signal();
    }

    private void Signal()
    {
        if (Interlocked.CompareExchange(ref _wakeSignaled, 1, 0) != 0) return;
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                await _wake.WaitAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _wakeSignaled, 0);

                // Control/state/input configuration is always prioritised over video.
                while (_control.TryDequeue(out var text))
                {
                    await SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, _cts.Token)
                        .ConfigureAwait(false);
                }

                while (true)
                {
                    byte[]? keyframe;
                    byte[]? delta;
                    lock (_videoLock)
                    {
                        keyframe = _pendingKeyframe;
                        delta = _pendingDelta;
                        _pendingKeyframe = null;
                        _pendingDelta = null;
                    }

                    if (keyframe is null && delta is null) break;
                    if (keyframe is not null)
                        await SendAsync(keyframe, WebSocketMessageType.Binary, _cts.Token).ConfigureAwait(false);
                    if (delta is not null)
                        await SendAsync(delta, WebSocketMessageType.Binary, _cts.Token).ConfigureAwait(false);
                }

                while (_audio.TryDequeue(out var audio))
                    await SendAsync(audio, WebSocketMessageType.Binary, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            // The receive loop notices the socket close and removes the client.
        }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            lock (_videoLock)
            {
                _pendingKeyframe = null;
                _pendingDelta = null;
            }
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "send loop ended", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task SendAsync(byte[] payload, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open) return;
        await _socket.SendAsync(new ArraySegment<byte>(payload), type, true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReceiveLoopAsync(Func<ClientConnection, string, Task> onText, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { }
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 1024 * 1024)
                        throw new InvalidOperationException("客户端消息超过 1 MiB 限制。");
                } while (!result.EndOfMessage);

                if (message.Length == 0) continue;
                var text = System.Text.Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                await onText(this, text).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            _cts.Cancel();
        }
    }

    public void RequestClose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        RequestClose();
        if (_sendLoop is not null)
        {
            try { await _sendLoop.ConfigureAwait(false); } catch { }
        }
        _wake.Dispose();
        _cts.Dispose();
        _socket.Dispose();
    }
}
