namespace LinearTv.Builder.Assemble;

/// <summary>Reads the few MPEG-TS header fields the assembler needs. Every method takes one 188-byte packet.</summary>
internal static class TsPacket
{
    public const int Size = 188;
    public const int PatPid = 0;

    public static int Pid(ReadOnlySpan<byte> p) => ((p[1] & 0x1F) << 8) | p[2];

    public static bool PayloadUnitStart(ReadOnlySpan<byte> p) => (p[1] & 0x40) != 0;

    private static bool HasAdaptation(ReadOnlySpan<byte> p) => (p[3] & 0x20) != 0;

    private static bool HasPayload(ReadOnlySpan<byte> p) => (p[3] & 0x10) != 0;

    /// <summary>Whether the packet is flagged as a random access point: for video, a keyframe starts here.</summary>
    public static bool RandomAccess(ReadOnlySpan<byte> p) => HasAdaptation(p) && p[4] > 0 && (p[5] & 0x40) != 0;

    private static int PayloadStart(ReadOnlySpan<byte> p) => HasAdaptation(p) ? 5 + p[4] : 4;

    /// <summary>The decode timestamp (else presentation timestamp) of a PES starting in this packet, 90 kHz.</summary>
    public static bool TryDecodeTimestamp(ReadOnlySpan<byte> p, out long timestamp)
    {
        timestamp = 0;
        if (!PayloadUnitStart(p) || !HasPayload(p))
        {
            return false;
        }

        var i = PayloadStart(p);
        if (i + 19 > Size || p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 1)
        {
            return false;
        }

        var flags = p[i + 7] >> 6;
        if (flags < 2)
        {
            return false;
        }

        timestamp = ReadTimestamp(p, i + (flags == 3 ? 14 : 9));
        return true;
    }

    /// <summary>The PMT's PID, from a PAT packet (the first programme listed).</summary>
    public static int? PmtPid(ReadOnlySpan<byte> p)
    {
        var section = Section(p);
        // table_id 0, then program entries of 4 bytes after the 8-byte header; skip program 0 (NIT).
        if (section.Length < 12 || section[0] != 0x00)
        {
            return null;
        }

        var length = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(3 + length - 4, section.Length);
        for (var i = 8; i + 4 <= end; i += 4)
        {
            var program = (section[i] << 8) | section[i + 1];
            if (program != 0)
            {
                return ((section[i + 2] & 0x1F) << 8) | section[i + 3];
            }
        }

        return null;
    }

    /// <summary>The elementary streams a PMT packet lists, as (stream type, PID).</summary>
    public static List<(int StreamType, int Pid)> PmtStreams(ReadOnlySpan<byte> p)
    {
        var result = new List<(int, int)>();
        var section = Section(p);
        if (section.Length < 12 || section[0] != 0x02)
        {
            return result;
        }

        var length = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(3 + length - 4, section.Length);
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        for (var i = 12 + programInfoLength; i + 5 <= end;)
        {
            var type = section[i];
            var pid = ((section[i + 1] & 0x1F) << 8) | section[i + 2];
            var infoLength = ((section[i + 3] & 0x0F) << 8) | section[i + 4];
            result.Add((type, pid));
            i += 5 + infoLength;
        }

        return result;
    }

    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> p)
    {
        if (!PayloadUnitStart(p) || !HasPayload(p))
        {
            return [];
        }

        var i = PayloadStart(p);
        if (i >= Size)
        {
            return [];
        }

        var start = i + 1 + p[i]; // pointer field
        return start < Size ? p[start..] : [];
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> p, int i)
        => (long)((((ulong)p[i] >> 1 & 0x07UL) << 30) | ((ulong)p[i + 1] << 22) | (((ulong)p[i + 2] >> 1) << 15)
            | ((ulong)p[i + 3] << 7) | ((ulong)p[i + 4] >> 1));
}
