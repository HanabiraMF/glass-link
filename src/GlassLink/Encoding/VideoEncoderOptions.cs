namespace GlassLink.Encoding;

public sealed record VideoEncoderOptions(
    int Width,
    int Height,
    int Fps,
    int Bitrate,
    int Gop,
    bool AllowSoftwareFallback)
{
    public static VideoEncoderOptions Default(int width, int height, int fps = 60, int bitrate = 16_000_000) =>
        new(width, height, Math.Clamp(fps, 15, 240), Math.Max(1_000_000, bitrate), Math.Clamp(fps * 2, 30, 600), true);
}

public sealed record VideoStreamConfig(
    string Codec,
    int Width,
    int Height,
    int Fps,
    int Bitrate,
    string EncoderName,
    bool HardwareAccelerated,
    string Format = "annexb");

public sealed record EncodedVideoFrame(
    byte[] Data,
    bool IsKeyframe,
    long TimestampMicroseconds,
    uint DurationMicroseconds);
