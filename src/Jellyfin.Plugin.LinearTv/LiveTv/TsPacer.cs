namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Paces an MPEG-TS stream to real time by its PCR (the stream's own system clock), after an
/// initial burst sent as fast as possible.
/// </summary>
/// <remarks>
/// <para>
/// Why a burst: a browser gets HLS, and Jellyfin only lists a live HLS playlist once three
/// segments exist. Paced from the first frame (Jellyfin's <c>-re</c>), that takes three segment
/// lengths of real time: measured at ~31 s with 10 s keyframe intervals, which is also what
/// x264's default settings produce. With the burst, those segments exist almost immediately.
/// </para>
/// <para>
/// Why pace at all: the remuxes arrive far faster than real time. Unpaced, a reader races ahead
/// through the schedule, a TV fills minutes ahead of the guide, and Jellyfin's HLS remux writes
/// segments for programmes nobody has reached yet.
/// </para>
/// <para>
/// The viewer still sees the programme from the tune-in point. Jellyfin's web player starts HLS
/// from the beginning of the playlist, not the live edge. The burst only fills the buffer ahead.
/// </para>
/// </remarks>
internal sealed class TsPacer(TimeProvider time, TimeSpan burst)
{
    private const long Modulus = 1L << 33;
    private const long HalfModulus = 1L << 32;
    private const long PcrHz = 90_000;

    private long _startTimestamp;
    private long? _lastPcr;
    private long _elapsedPcr; // 90 kHz ticks of stream time sent so far, unwrapped

    /// <summary>How long to wait before sending this packet; zero if it can go now.</summary>
    public TimeSpan DelayFor(ReadOnlySpan<byte> packet)
    {
        if (!TryReadPcr(packet, out var pcr))
        {
            return TimeSpan.Zero;
        }

        if (_lastPcr is not { } last)
        {
            return TimeSpan.Zero;
        }

        var streamTime = TimeSpan.FromSeconds((double)(_elapsedPcr + Forward(pcr, last)) / PcrHz);
        var wait = streamTime - burst - time.GetElapsedTime(_startTimestamp);
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Records that a packet was sent.</summary>
    public void Sent(ReadOnlySpan<byte> packet)
    {
        if (!TryReadPcr(packet, out var pcr))
        {
            return;
        }

        if (_lastPcr is { } last)
        {
            _elapsedPcr += Forward(pcr, last);
        }
        else
        {
            // The stream's clock starts with its first PCR.
            _startTimestamp = time.GetTimestamp();
        }

        _lastPcr = pcr;
    }

    // Distance from last to pcr on the 33-bit circle. A backwards step (none are expected once
    // TsSplicer has joined programmes) counts as no time, rather than as 26 hours.
    private static long Forward(long pcr, long last)
    {
        var delta = (((pcr - last) % Modulus) + Modulus) % Modulus;
        return delta < HalfModulus ? delta : 0;
    }

    private static bool TryReadPcr(ReadOnlySpan<byte> p, out long pcrBase)
    {
        pcrBase = 0;
        const int PcrFlag = 0x10;
        if (p.Length < 12 || p[0] != 0x47 || (p[3] & 0x20) == 0 || p[4] < 7 || (p[5] & PcrFlag) == 0)
        {
            return false;
        }

        pcrBase = (long)(((ulong)p[6] << 25) | ((ulong)p[7] << 17) | ((ulong)p[8] << 9) | ((ulong)p[9] << 1) | ((ulong)p[10] >> 7));
        return true;
    }
}
