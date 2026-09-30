using Jellyfin.Plugin.LinearTv.LiveTv;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class TsPacerTests
{
    private const long Hz = 90_000;
    private static readonly TimeSpan Burst = TimeSpan.FromSeconds(35);

    private sealed class ManualClock : TimeProvider
    {
        public long Now { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now;

        public void Advance(TimeSpan by) => Now += by.Ticks;
    }

    private static byte[] PcrPacket(long pcrBase)
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[3] = 0x30;
        p[4] = 7;
        p[5] = 0x10;
        p[6] = (byte)(pcrBase >> 25);
        p[7] = (byte)(pcrBase >> 17);
        p[8] = (byte)(pcrBase >> 9);
        p[9] = (byte)(pcrBase >> 1);
        p[10] = (byte)(((pcrBase & 1) << 7) | 0x7E);
        return p;
    }

    private static byte[] PlainPacket()
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[3] = 0x10;
        return p;
    }

    private static TimeSpan Send(TsPacer pacer, byte[] packet)
    {
        var wait = pacer.DelayFor(packet);
        if (wait == TimeSpan.Zero)
        {
            pacer.Sent(packet);
        }

        return wait;
    }

    [Fact]
    public void TheBurst_GoesOutImmediately()
    {
        var pacer = new TsPacer(new ManualClock(), Burst);
        const long start = 90_000;

        // Stream time 0 .. 35 s, with the wall clock not moving at all.
        for (var s = 0; s <= 35; s++)
        {
            Assert.Equal(TimeSpan.Zero, Send(pacer, PcrPacket(start + (s * Hz))));
        }
    }

    [Fact]
    public void AfterTheBurst_PacketsWaitForRealTime()
    {
        var clock = new ManualClock();
        var pacer = new TsPacer(clock, Burst);
        Send(pacer, PcrPacket(0));

        // 40 s of stream time, 0 s of wall time: 5 s beyond the burst.
        Assert.Equal(TimeSpan.FromSeconds(5), pacer.DelayFor(PcrPacket(40 * Hz)));

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(2), pacer.DelayFor(PcrPacket(40 * Hz)));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, pacer.DelayFor(PcrPacket(40 * Hz)));
    }

    [Fact]
    public void SteadyState_RunsAtRealTime()
    {
        var clock = new ManualClock();
        var pacer = new TsPacer(clock, Burst);
        Send(pacer, PcrPacket(0));
        Send(pacer, PcrPacket(35 * Hz)); // the burst is spent

        // Every further second of stream needs a second of wall clock.
        for (var s = 36; s < 100; s++)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), Send(pacer, PcrPacket(s * Hz)));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.Zero, Send(pacer, PcrPacket(s * Hz)));
        }
    }

    [Fact]
    public void PacketsWithoutPcr_AreNeverHeld()
    {
        var pacer = new TsPacer(new ManualClock(), Burst);
        Send(pacer, PcrPacket(0));
        Send(pacer, PcrPacket(3600 * Hz)); // far ahead: the next PCR packet would have to wait

        Assert.Equal(TimeSpan.Zero, pacer.DelayFor(PlainPacket()));
    }

    [Fact]
    public void TheClockWrap_IsNotTreatedAs26Hours()
    {
        // No burst, so the answer is exactly the stream time: mishandling the wrap would give
        // either 0 (treated as backwards) or ~26.5 hours.
        var pacer = new TsPacer(new ManualClock(), TimeSpan.Zero);
        const long nearWrap = (1L << 33) - (10 * Hz);

        Send(pacer, PcrPacket(nearWrap));

        // 10 s before the wrap to 10 s after it: 20 s of stream time.
        Assert.Equal(TimeSpan.FromSeconds(20), pacer.DelayFor(PcrPacket(10 * Hz)));
    }
}
