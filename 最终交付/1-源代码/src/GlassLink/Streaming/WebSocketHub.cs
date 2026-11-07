using System.Collections.Concurrent;
using System.Net.WebSockets;
using GlassLink.Protocol;
using Microsoft.Extensions.Logging;

namespace GlassLink.Streaming;

public sealed record ClientSnapshot(string Id, string Role, string Name, DateTimeOffset ConnectedAt);

/// <summary>
/// Owns connected browser clients. Control messages are reliable and prioritised;
/// video is sent on a small bounded per-client queue with DropOldest so a slow
/// phone can never stall the capture/encode thread or other viewers.
/// </summary>
public sealed class WebSocketHub : IAsyncDisposable, IStreamTransport
{
    private readonly ILogger<WebSocketHub> _logger;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly Func<ClientConnection, Task> _onConnected;
    private readonly Func<ClientConnection, ClientMessage, Task> _onMessage;
    private readonly Func<ClientConnection, Task> _onDisconnected;

    public WebSocketHub(
        ILogger<WebSocketHub> logger,
        Func<ClientConnection, Task> onConnected,
        Func<ClientConnection, ClientMessage, Task> onMessage,
        Func<ClientConnection, Task> onDisconnected)
    {
        _logger = logger;
        _onConnected = onConnected;
        _onMessage = onMessage;
        _onDisconnected = onDisconnected;
    }

    public int Count => _clients.Count;
    public int ClientCount => _clients.Count;
    public IReadOnlyList<ClientSnapshot> Snapshot =>
        _clients.Values.Select(client => new ClientSnapshot(client.Id, client.Role, client.Name, client.ConnectedAt)).ToArray();

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var role = context.Request.Query["client"].ToString();
        if (role is not ("desktop" or "mobile")) role = "desktop";

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new ClientConnection(Guid.NewGuid().ToString("N")[..12], role, socket);
        _clients[client.Id] = client;
        client.StartSendLoop();
        _logger.LogInformation("Client connected: id={Id}, role={Role}, from={Remote}. Total={Total}",
            client.Id, role, context.Connection.RemoteIpAddress, _clients.Count);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await _onConnected(client).ConfigureAwait(false);
            await client.ReceiveLoopAsync(HandleTextAsync, connectionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket client pipeline ended with an error.");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            try { await _onDisconnected(client).ConfigureAwait(false); } catch { }
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Client disconnected: id={Id}. Total={Total}", client.Id, _clients.Count);
        }
    }

    private async Task HandleTextAsync(ClientConnection client, string text)
    {
        ClientMessage? message;
        try
        {
            message = ProtocolJson.Deserialize<ClientMessage>(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Invalid client JSON from {Id}.", client.Id);
            SendError(client, "消息格式无效。");
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
        {
            SendError(client, "消息缺少 type。");
            return;
        }

        try
        {
            await _onMessage(client, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling client message {Type}.", message.Type);
            SendError(client, ex.Message);
        }
    }

    public void BroadcastText(string json)
    {
        foreach (var client in _clients.Values)
            client.QueueControl(json);
    }

    public void BroadcastVideo(byte[] packet)
    {
        foreach (var client in _clients.Values)
            client.QueueVideo(packet);
    }

    public void BroadcastAudio(byte[] packet)
    {
        foreach (var client in _clients.Values)
            client.QueueAudio(packet);
    }

    public void SendText(ClientConnection client, string json) => client.QueueControl(json);

    public void SendVideo(ClientConnection client, byte[] packet) => client.QueueVideo(packet);

    public void BroadcastControl(string json) => BroadcastText(json);

    public void SendControl(string clientId, string json)
    {
        if (_clients.TryGetValue(clientId, out var client)) client.QueueControl(json);
    }

    public void SendVideo(string clientId, byte[] packet)
    {
        if (_clients.TryGetValue(clientId, out var client)) client.QueueVideo(packet);
    }

    public void SendError(ClientConnection client, string message) =>
        client.QueueControl(ProtocolJson.Serialize(new { type = "error", message }));

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}
