using System.Buffers.Binary;
using GlassLink.Encoding;

namespace GlassLink.Streaming;

/// <summary>
/// Binary WebSocket video packet:
///   0..3   "GLV1"
///   4      flags: bit0 = keyframe, bit1 = config change
///   5      codec: 1 = H.264 Annex-B
///   6..7   reserved
///   8..15  timestamp (microseconds, little-endian)
///   16..19 duration (microseconds)
///   20..23 payload length
///   24..   Annex-B H.264 access unit
/// </summary>
public static class VideoPacket
{
    public const int HeaderSize = 24;
    public const byte CodecH264 = 1;
    public const byte FlagKeyframe = 0x01;

    public static byte[] Build(EncodedVideoFrame frame)
    {
        var packet = new byte[HeaderSize + frame.Data.Length];
        packet[0] = (byte)'G';
        packet[1] = (byte)'L';
        packet[2] = (byte)'V';
        packet[3] = (byte)'1';
        packet[4] = frame.IsKeyframe ? FlagKeyframe : (byte)0;
        packet[5] = CodecH264;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8, 8), frame.TimestampMicroseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), frame.DurationMicroseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), (uint)frame.Data.Length);
        frame.Data.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }
}
