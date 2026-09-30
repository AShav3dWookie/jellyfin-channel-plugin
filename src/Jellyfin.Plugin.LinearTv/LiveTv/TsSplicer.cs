namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Joins consecutive MPEG-TS programmes into one continuous stream: shifts each programme's
/// timestamps (PTS, DTS, PCR) so its timeline follows on from the last, and keeps every PID's
/// continuity counter in sequence. Header bookkeeping only; no media is decoded or re-encoded.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: each programme is a separate Jellyfin remux whose timestamps start near zero.
/// Joined raw, the timeline jumps backwards at every programme change. Plain ffmpeg corrects that,
/// but Jellyfin's browser path remuxes with <c>-copyts</c>, which disables the correction. That
/// produced tens of thousands of "Non-monotonic DTS" errors and a broken HLS stream after the
/// first join (measured; docs/testing.md channels test F).
/// </para>
/// <para>
/// Each programme gets one offset, applied to every PID, so audio and video stay in sync. The
/// offset places the programme's earliest timestamp one frame after the latest timestamp already
/// sent. To find that earliest timestamp, a new programme's first packets are held back until
/// its first timestamps have been seen, then released rewritten, in order.
/// </para>
/// </remarks>
internal sealed class TsSplicer
{
    public const int PacketSize = 188;

    /// <summary>40 ms at 90 kHz: one frame at 25 fps between one programme and the next.</summary>
    public const long ProgrammeGap = 3600;

    private const byte SyncByte = 0x47;
    private const int NullPid = 0x1FFF;
    private const long Modulus = 1L << 33;   // PTS, DTS and PCR base are 33-bit, 90 kHz
    private const long HalfModulus = 1L << 32;

    // A new programme's offset is fixed once timestamps from this many elementary streams have
    // been seen (audio and video), or after this many packets if one never appears.
    private const int StreamsToSee = 2;
    private const int MaxLookaheadPackets = 2048;

    private readonly Queue<byte[]> _output = new();
    private readonly byte[] _partial = new byte[PacketSize];
    private readonly List<byte[]> _pending = [];
    private readonly Dictionary<int, long> _firstTimestamps = [];
    private readonly Dictionary<int, int> _nextContinuity = [];

    private byte[]? _head;
    private int _headOffset;
    private int _partialLength;
    private bool _started;
    private bool _offsetKnown = true;
    private long _offset;
    private long? _latest;

    /// <summary>Gets the number of bytes discarded as not being valid, whole TS packets.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Marks the start of the next programme's bytes.</summary>
    public void BeginProgramme()
    {
        DropPartial();
        _pending.Clear();
        _firstTimestamps.Clear();

        // The first programme is passed through at its own timestamps; each later one is placed
        // after whatever has been sent.
        _offsetKnown = !_started;
        _started = true;
    }

    /// <summary>Marks the end of the current programme's bytes.</summary>
    public void EndProgramme()
    {
        DropPartial();
        if (!_offsetKnown)
        {
            FixOffsetAndRelease();
        }
    }

    /// <summary>Accepts bytes of the current programme, in any chunking.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_partialLength > 0 || data.Length < PacketSize)
            {
                var take = Math.Min(PacketSize - _partialLength, data.Length);
                data[..take].CopyTo(_partial.AsSpan(_partialLength));
                _partialLength += take;
                data = data[take..];
                if (_partialLength == PacketSize)
                {
                    Accept(_partial.ToArray());
                    _partialLength = 0;
                }

                continue;
            }

            Accept(data[..PacketSize].ToArray());
            data = data[PacketSize..];
        }
    }

    /// <summary>Gets a value indicating whether a packet has been partly read out.</summary>
    public bool HasPartialPacket => _head is not null;

    /// <summary>
    /// The next whole packet ready to send, without removing it; null if there is none. Only
    /// meaningful when <see cref="HasPartialPacket"/> is false.
    /// </summary>
    public byte[]? PeekPacket() => _head is null && _output.TryPeek(out var packet) ? packet : null;

    /// <summary>
    /// Copies out bytes of one packet only (finishing a partly-read one, else starting the next),
    /// so a caller can pace the stream at packet boundaries.
    /// </summary>
    public int ReadPacket(Span<byte> destination) => Read(destination, stopAtPacketEnd: true);

    /// <summary>Copies out rewritten bytes ready to send. Returns 0 when none are ready yet.</summary>
    public int Read(Span<byte> destination) => Read(destination, stopAtPacketEnd: false);

    private int Read(Span<byte> destination, bool stopAtPacketEnd)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (_head is null)
            {
                if (!_output.TryDequeue(out _head))
                {
                    break;
                }

                _headOffset = 0;
            }

            var count = Math.Min(_head.Length - _headOffset, destination.Length - written);
            _head.AsSpan(_headOffset, count).CopyTo(destination[written..]);
            written += count;
            _headOffset += count;
            if (_headOffset == _head.Length)
            {
                _head = null;
                if (stopAtPacketEnd)
                {
                    break;
                }
            }
        }

        return written;
    }

    private void Accept(byte[] packet)
    {
        if (packet[0] != SyncByte)
        {
            // Jellyfin's remux output is packet-aligned; anything else is not a TS packet.
            DroppedBytes += PacketSize;
            return;
        }

        if (_offsetKnown)
        {
            Emit(packet);
            return;
        }

        _pending.Add(packet);
        if (PesTimestamp(packet, out var timestamp) is var pid && pid >= 0)
        {
            _firstTimestamps.TryAdd(pid, timestamp);
        }

        if (_firstTimestamps.Count >= StreamsToSee || _pending.Count >= MaxLookaheadPackets)
        {
            FixOffsetAndRelease();
        }
    }

    private void FixOffsetAndRelease()
    {
        if (_latest is { } latest && _firstTimestamps.Count > 0)
        {
            var earliest = _firstTimestamps.Values.Aggregate((a, b) => IsLater(a, b) ? b : a);
            _offset = Mod(latest + ProgrammeGap - earliest);
        }

        // With no timestamps found at all, keep the previous offset: better than guessing.
        _offsetKnown = true;
        foreach (var packet in _pending)
        {
            Emit(packet);
        }

        _pending.Clear();
    }

    private void Emit(byte[] packet)
    {
        Rewrite(packet);
        _output.Enqueue(packet);
    }

    private void Rewrite(byte[] packet)
    {
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x3;
        var hasAdaptation = (adaptationControl & 0x2) != 0;
        var hasPayload = (adaptationControl & 0x1) != 0;

        if (pid != NullPid)
        {
            // Counters restart in every remux; continue them instead, or decoders flag the join as
            // packet loss. Packets without payload repeat the previous counter rather than advance.
            var counter = packet[3] & 0x0F;
            if (_nextContinuity.TryGetValue(pid, out var next))
            {
                counter = hasPayload ? next : (next + 15) & 0x0F;
                if (hasPayload)
                {
                    _nextContinuity[pid] = (next + 1) & 0x0F;
                }
            }
            else
            {
                _nextContinuity[pid] = (counter + 1) & 0x0F;
            }

            packet[3] = (byte)((packet[3] & 0xF0) | counter);
        }

        var payloadStart = 4;
        if (hasAdaptation)
        {
            int adaptationLength = packet[4];
            payloadStart = 5 + adaptationLength;
            const int PcrFlag = 0x10;
            if (adaptationLength >= 7 && (packet[5] & PcrFlag) != 0)
            {
                WritePcrBase(packet, 6, Mod(ReadPcrBase(packet, 6) + _offset));
            }
        }

        if (!hasPayload || !IsPesStart(packet, payloadStart, out var flags))
        {
            return;
        }

        long decode = -1;
        if (flags >= 2)
        {
            var pts = Mod(ReadTimestamp(packet, payloadStart + 9) + _offset);
            WriteTimestamp(packet, payloadStart + 9, pts);
            decode = pts;
        }

        if (flags == 3)
        {
            var dts = Mod(ReadTimestamp(packet, payloadStart + 14) + _offset);
            WriteTimestamp(packet, payloadStart + 14, dts);
            decode = dts;
        }

        if (decode >= 0 && (_latest is not { } latest || IsLater(decode, latest)))
        {
            _latest = decode;
        }
    }

    /// <summary>The PID of a packet that starts a PES with a timestamp, or -1.</summary>
    private static int PesTimestamp(byte[] packet, out long timestamp)
    {
        timestamp = 0;
        var adaptationControl = (packet[3] >> 4) & 0x3;
        if ((adaptationControl & 0x1) == 0)
        {
            return -1;
        }

        var payloadStart = (adaptationControl & 0x2) != 0 ? 5 + packet[4] : 4;
        if (!IsPesStart(packet, payloadStart, out var flags) || flags < 2)
        {
            return -1;
        }

        // Decode order is what must stay monotonic, so prefer DTS.
        timestamp = ReadTimestamp(packet, payloadStart + (flags == 3 ? 14 : 9));
        return ((packet[1] & 0x1F) << 8) | packet[2];
    }

    /// <summary>
    /// Whether the payload begins a PES packet with an optional header, and its PTS/DTS flags
    /// (2 = PTS only, 3 = PTS and DTS).
    /// </summary>
    private static bool IsPesStart(byte[] packet, int payloadStart, out int flags)
    {
        flags = 0;
        const int PayloadUnitStart = 0x40;
        if ((packet[1] & PayloadUnitStart) == 0 || payloadStart + 19 > PacketSize)
        {
            return false;
        }

        if (packet[payloadStart] != 0 || packet[payloadStart + 1] != 0 || packet[payloadStart + 2] != 1)
        {
            return false;
        }

        // Stream ids without the optional PES header, and so without timestamps.
        switch (packet[payloadStart + 3])
        {
            case 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF:
                return false;
        }

        flags = packet[payloadStart + 7] >> 6;
        return true;
    }

    private static long ReadTimestamp(byte[] p, int i)
        => (long)((((ulong)p[i] >> 1 & 0x07UL) << 30) | ((ulong)p[i + 1] << 22) | (((ulong)p[i + 2] >> 1) << 15)
            | ((ulong)p[i + 3] << 7) | ((ulong)p[i + 4] >> 1));

    private static void WriteTimestamp(byte[] p, int i, long ts)
    {
        // Keep the 4-bit prefix and the marker bits.
        p[i] = (byte)((p[i] & 0xF1) | ((int)((ts >> 30) & 0x07) << 1));
        p[i + 1] = (byte)(ts >> 22);
        p[i + 2] = (byte)((((ts >> 15) & 0x7F) << 1) | 1);
        p[i + 3] = (byte)(ts >> 7);
        p[i + 4] = (byte)(((ts & 0x7F) << 1) | 1);
    }

    private static long ReadPcrBase(byte[] p, int i)
        => (long)(((ulong)p[i] << 25) | ((ulong)p[i + 1] << 17) | ((ulong)p[i + 2] << 9) | ((ulong)p[i + 3] << 1) | ((ulong)p[i + 4] >> 7));

    private static void WritePcrBase(byte[] p, int i, long pcrBase)
    {
        // Keep the 6 reserved bits and the 9-bit extension, which follow the base.
        p[i] = (byte)(pcrBase >> 25);
        p[i + 1] = (byte)(pcrBase >> 17);
        p[i + 2] = (byte)(pcrBase >> 9);
        p[i + 3] = (byte)(pcrBase >> 1);
        p[i + 4] = (byte)(((int)(pcrBase & 0x1) << 7) | (p[i + 4] & 0x7F));
    }

    private static long Mod(long value) => ((value % Modulus) + Modulus) % Modulus;

    // True when a is after b on the 33-bit circle, i.e. across a wrap as well.
    private static bool IsLater(long a, long b) => Mod(a - b) is > 0 and < HalfModulus;

    private void DropPartial()
    {
        DroppedBytes += _partialLength;
        _partialLength = 0;
    }
}
