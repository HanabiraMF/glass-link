using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassLink.Protocol;

public sealed class ClientMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("layer")]
    public string? Layer { get; set; }

    [JsonPropertyName("event")]
    public InputEvent? Event { get; set; }

    [JsonPropertyName("t")]
    public long? T { get; set; }

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed record ControllerDto(
    string Requested,
    string Active,
    string ActiveDisplayName,
    bool HardwareAvailable,
    string? FallbackReason,
    IReadOnlyList<string> Diagnostics,
    DateTimeOffset ChangedAtUtc);

public sealed record VideoInfoDto(
    string Codec,
    int Width,
    int Height,
    int Fps,
    int Bitrate,
    string EncoderName,
    bool HardwareAccelerated,
    string Format);

public sealed record StreamStatsDto(
    double Fps,
    double MegabitsPerSecond,
    long FramesSent,
    long BytesSent);

public sealed record ServerStatusDto(
    bool Streaming,
    string StreamState,
    string? StreamError,
    int Clients,
    bool SecureContextAvailable,
    string HttpUrl,
    string HttpsUrl,
    ControllerDto Controller,
    VideoInfoDto? Video,
    StreamStatsDto Stats);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
