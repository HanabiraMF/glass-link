using System.Buffers.Binary;

namespace GlassLink.Encoding;

/// <summary>
/// Small H.264 Annex-B helpers. Media Foundation's H.264 MFT emits a byte stream
/// (Annex-B) and exposes SPS/PPS in MF_MT_MPEG_SEQUENCE_HEADER. WebCodecs accepts
/// Annex-B when the decoder is configured with avc.format = "annexb".
/// </summary>
public static class H264Utilities
{
    public readonly record struct NalUnit(int Offset, int Length, int Type);

    public static byte[] NormalizeAnnexB(ReadOnlySpan<byte> input)
    {
        if (input.Length == 0) return Array.Empty<byte>();
        if (IsAnnexB(input)) return input.ToArray();

        // Detect AVCC 4-byte length-prefixed NAL units and convert to Annex-B.
        var avcc = TryParseAvcc(input);
        if (avcc.Count > 0)
        {
            using var stream = new MemoryStream(input.Length + avcc.Count * 4);
            foreach (var (offset, length) in avcc)
            {
                stream.Write(new byte[] { 0, 0, 0, 1 });
                stream.Write(input.Slice(offset, length));
            }
            return stream.ToArray();
        }

        // A single raw NAL unit without a start code.
        var result = new byte[input.Length + 4];
        result[3] = 1;
        input.CopyTo(result.AsSpan(4));
        return result;
    }

    public static bool IsAnnexB(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3) return false;
        if (data[0] == 0 && data[1] == 0 && data[2] == 1) return true;
        return data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
    }

    public static List<NalUnit> EnumerateNalUnits(ReadOnlySpan<byte> data)
    {
        var result = new List<NalUnit>();
        var position = 0;
        while (position + 3 < data.Length)
        {
            var start = FindStartCode(data, position, out var prefixLength);
            if (start < 0) break;
            var nalStart = start + prefixLength;
            var next = FindStartCode(data, nalStart, out _);
            var nalEnd = next < 0 ? data.Length : next;
            var length = nalEnd - nalStart;
            if (length > 0)
            {
                var type = data[nalStart] & 0x1F;
                result.Add(new NalUnit(nalStart, length, type));
            }
            position = nalEnd;
        }
        return result;
    }

    public static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        foreach (var nal in EnumerateNalUnits(data))
        {
            if (nal.Type == 5) return true;
        }
        return false;
    }

    public static bool ContainsSpsAndPps(ReadOnlySpan<byte> data)
    {
        var hasSps = false;
        var hasPps = false;
        foreach (var nal in EnumerateNalUnits(data))
        {
            if (nal.Type == 7) hasSps = true;
            if (nal.Type == 8) hasPps = true;
        }
        return hasSps && hasPps;
    }

    public static byte[] ExtractParameterSets(ReadOnlySpan<byte> data)
    {
        var nals = EnumerateNalUnits(data);
        using var stream = new MemoryStream();
        foreach (var nal in nals)
        {
            if (nal.Type is not (7 or 8)) continue;
            stream.Write(new byte[] { 0, 0, 0, 1 });
            stream.Write(data.Slice(nal.Offset, nal.Length));
        }
        return stream.ToArray();
    }

    public static string BuildCodecString(ReadOnlySpan<byte> spsAnnexB)
    {
        foreach (var nal in EnumerateNalUnits(spsAnnexB))
        {
            if (nal.Type != 7 || nal.Length < 4) continue;
            var offset = nal.Offset;
            var profile = spsAnnexB[offset + 1];
            var constraints = spsAnnexB[offset + 2];
            var level = spsAnnexB[offset + 3];
            return $"avc1.{profile:X2}{constraints:X2}{level:X2}";
        }

        // Conservative fallback: Main profile, level 4.2.
        return "avc1.4D002A";
    }

    public static byte[] EnsureParameterSets(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> parameterSets)
    {
        if (parameterSets.Length == 0 || ContainsSpsAndPps(frame))
            return frame.ToArray();

        var result = new byte[parameterSets.Length + frame.Length];
        parameterSets.CopyTo(result);
        frame.CopyTo(result.AsSpan(parameterSets.Length));
        return result;
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int prefixLength)
    {
        for (var i = Math.Max(0, from); i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1)
            {
                prefixLength = 3;
                return i;
            }
            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                prefixLength = 4;
                return i;
            }
        }
        prefixLength = 0;
        return -1;
    }

    private static List<(int Offset, int Length)> TryParseAvcc(ReadOnlySpan<byte> data)
    {
        var result = new List<(int Offset, int Length)>();
        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length <= 0 || offset + 4 + length > data.Length)
            {
                result.Clear();
                return result;
            }
            result.Add((offset + 4, length));
            offset += 4 + length;
        }
        return offset == data.Length ? result : new List<(int, int)>();
    }
}
