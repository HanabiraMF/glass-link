namespace GlassLink.Streaming;

/// <summary>
/// Transport-neutral video/control fan-out boundary.
///
/// The v1 delivery uses WebSocket, but the video packet format (GLV1 + H.264
/// Annex-B) and the JSON control messages are independent of WebSocket. A future
/// WebTransport implementation can implement this interface and reuse the
/// capture, encoder, input model and browser WebCodecs decoder unchanged.
/// </summary>
public interface IStreamTransport
{
    int ClientCount { get; }
    void BroadcastControl(string json);
    void BroadcastVideo(byte[] packet);
    void SendControl(string clientId, string json);
    void SendVideo(string clientId, byte[] packet);
}
