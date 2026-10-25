using System.Buffers.Binary;

namespace Jellyfin.Plugin.LinearTv.Store;

/// <summary>
/// Where each video keyframe of a channel is: its time in the channel's timeline and the byte
/// offset of its first packet across the channel's files end to end. Tuning in reads from the
/// keyframe at or before "now", so playback starts on a picture the decoder can show.
/// Shared source: the builder links this file.
/// </summary>
/// <remarks>
/// File format: the 8 bytes <c>LTVIDX01</c>, an int64 entry count, then per entry an int64 time
/// (100 ns ticks) and an int64 byte offset, all little-endian, in increasing order of both.
/// </remarks>
internal sealed class KeyframeIndex
{
    private static readonly byte[] Magic = "LTVIDX01"u8.ToArray();

    private readonly long[] _ticks;
    private readonly long[] _offsets;

    public KeyframeIndex(long[] ticks, long[] offsets)
    {
        if (ticks.Length != offsets.Length)
        {
            throw new ArgumentException("Every keyframe needs a time and an offset.", nameof(offsets));
        }

        _ticks = ticks;
        _offsets = offsets;
    }

    public int Count => _ticks.Length;

    /// <summary>The last keyframe at or before <paramref name="ticks"/>; the first if none is.</summary>
    public (long Ticks, long Offset) AtOrBefore(long ticks)
    {
        if (_ticks.Length == 0)
        {
            return (0, 0);
        }

        var i = Array.BinarySearch(_ticks, ticks);
        if (i < 0)
        {
            i = Math.Max(0, ~i - 1);
        }

        return (_ticks[i], _offsets[i]);
    }

    public static KeyframeIndex Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Linear TV keyframe index.");
        }

        var count = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        if (count is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException($"Keyframe index claims {count} entries.");
        }

        var ticks = new long[count];
        var offsets = new long[count];
        Span<byte> entry = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(entry);
            ticks[i] = BinaryPrimitives.ReadInt64LittleEndian(entry);
            offsets[i] = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
        }

        return new KeyframeIndex(ticks, offsets);
    }

    public static KeyframeIndex ReadFile(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 16);
        return Read(stream);
    }

    public static void Write(Stream stream, IReadOnlyList<(long Ticks, long Offset)> entries)
    {
        Span<byte> buffer = stackalloc byte[16];
        stream.Write(Magic);
        BinaryPrimitives.WriteInt64LittleEndian(buffer, entries.Count);
        stream.Write(buffer[..8]);
        foreach (var (ticks, offset) in entries)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, ticks);
            BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], offset);
            stream.Write(buffer);
        }
    }
}
