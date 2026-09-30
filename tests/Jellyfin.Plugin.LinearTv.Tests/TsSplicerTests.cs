using Jellyfin.Plugin.LinearTv.LiveTv;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class TsSplicerTests
{
    private const int Video = 0x100;
    private const int Audio = 0x101;
    private const long Frame = 3600; // 40 ms at 90 kHz
    private const long Modulus = 1L << 33;

    // ---- a programme as a remux produces it: timestamps starting near 1.4 s ----

    private static List<byte[]> Programme(long start, int frames, bool withPcr = true)
    {
        var packets = new List<byte[]> { Packet(0, pusi: true) }; // PAT-like packet, no timestamps
        for (var i = 0; i < frames; i++)
        {
            var t = start + (i * Frame);
            packets.Add(Packet(Video, pusi: true, pcr: withPcr && i == 0 ? t - 63000 : null, pts: t + 7200, dts: t));
            packets.Add(Packet(Audio, pusi: true, pts: t));
        }

        return packets;
    }

    private static List<byte[]> Splice(params List<byte[]>[] programmes)
    {
        var splicer = new TsSplicer();
        var output = new List<byte[]>();
        foreach (var programme in programmes)
        {
            splicer.BeginProgramme();
            foreach (var packet in programme)
            {
                splicer.Write(packet);
            }

            splicer.EndProgramme();
        }

        var bytes = new byte[1 << 20];
        var length = splicer.Read(bytes);
        for (var i = 0; i < length; i += TsSplicer.PacketSize)
        {
            output.Add(bytes[i..(i + TsSplicer.PacketSize)]);
        }

        return output;
    }

    [Fact]
    public void FirstProgramme_PassesThroughAtItsOwnTimestamps()
    {
        var programme = Programme(126000, 5);

        var output = Splice(programme);

        Assert.Equal(programme.Count, output.Count);
        Assert.Equal(programme.Select(Dts), output.Select(Dts));
    }

    [Fact]
    public void SecondProgramme_StartsOneFrameAfterTheFirstEnds()
    {
        var first = Programme(126000, 10);
        var second = Programme(126000, 10); // restarts near zero, as every remux does

        var output = Splice(first, second);

        var firstOut = output.Take(first.Count).ToList();
        var secondOut = output.Skip(first.Count).ToList();
        var latest = firstOut.Select(Decode).Where(t => t >= 0).Max();
        var secondStart = secondOut.Select(Decode).Where(t => t >= 0).Min();

        Assert.Equal(latest + TsSplicer.ProgrammeGap, secondStart);
    }

    [Fact]
    public void EveryStream_IsMonotonicAcrossTheJoin()
    {
        var output = Splice(Programme(126000, 10), Programme(126000, 10), Programme(126000, 10));

        foreach (var pid in new[] { Video, Audio })
        {
            var timeline = output.Where(p => Pid(p) == pid).Select(Decode).ToList();
            for (var i = 1; i < timeline.Count; i++)
            {
                Assert.True(timeline[i] > timeline[i - 1], $"PID {pid:X}: {timeline[i]} after {timeline[i - 1]}");
            }
        }
    }

    [Fact]
    public void AudioAndVideo_StayInSync()
    {
        var first = Programme(126000, 10);
        var output = Splice(first, Programme(126000, 10));

        // Within the second programme, audio PTS and video DTS were equal going in; one shared
        // offset keeps them equal coming out.
        var second = output.Skip(first.Count).ToList();
        var video = second.Where(p => Pid(p) == Video).Select(Dts).ToList();
        var audio = second.Where(p => Pid(p) == Audio).Select(Pts).ToList();

        Assert.Equal(video, audio);
    }

    [Fact]
    public void Pcr_MovesWithTheTimestamps()
    {
        var first = Programme(126000, 10);
        var second = Programme(126000, 10);
        var shift = Splice(first, second)[first.Count + 1];                   // second programme's first video packet
        var original = second[1];

        Assert.Equal(Dts(shift) - Dts(original), Pcr(shift) - Pcr(original));
    }

    [Fact]
    public void ContinuityCounters_ContinueAcrossTheJoin()
    {
        var output = Splice(Programme(126000, 7), Programme(126000, 7));

        foreach (var pid in new[] { 0, Video, Audio })
        {
            var counters = output.Where(p => Pid(p) == pid).Select(p => p[3] & 0x0F).ToList();
            for (var i = 1; i < counters.Count; i++)
            {
                Assert.Equal((counters[i - 1] + 1) & 0x0F, counters[i]);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(187)]
    [InlineData(189)]
    [InlineData(1000)]
    public void Output_DoesNotDependOnHowTheBytesArrive(int chunk)
    {
        var programmes = new[] { Programme(126000, 6), Programme(126000, 6) };
        var whole = Splice(programmes).SelectMany(p => p).ToArray();

        var splicer = new TsSplicer();
        foreach (var programme in programmes)
        {
            splicer.BeginProgramme();
            var bytes = programme.SelectMany(p => p).ToArray();
            for (var i = 0; i < bytes.Length; i += chunk)
            {
                splicer.Write(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
            }

            splicer.EndProgramme();
        }

        var chunked = new byte[whole.Length + 1];
        Assert.Equal(whole.Length, splicer.Read(chunked));
        Assert.Equal(whole, chunked[..whole.Length]);
    }

    [Fact]
    public void Timestamps_WrapAt33Bits()
    {
        // A channel wraps its 33-bit clock every 26.5 hours. The first programme's third frame
        // lands exactly on the wrap, so its latest timestamp is 0, not 2^33.
        var first = Programme(Modulus - (2 * Frame), 3);
        var output = Splice(first, Programme(126000, 3));

        var secondStart = output.Skip(first.Count).Select(Decode).Where(t => t >= 0).Min();

        Assert.Equal(TsSplicer.ProgrammeGap, secondStart);
    }

    [Fact]
    public void PacketsHeldBackWhileFindingTheOffset_KeepTheirOrder()
    {
        var first = Programme(126000, 4);
        var second = Programme(126000, 4);

        var output = Splice(first, second).Skip(first.Count).Select(Pid);

        Assert.Equal(second.Select(Pid), output);
    }

    [Fact]
    public void APartialPacket_IsDroppedNotPassedOn()
    {
        var splicer = new TsSplicer();
        splicer.BeginProgramme();
        splicer.Write(Programme(126000, 1)[0]);
        splicer.Write(new byte[] { 0x47, 0, 0 });
        splicer.EndProgramme();

        Assert.Equal(TsSplicer.PacketSize, splicer.Read(new byte[1000]));
        Assert.Equal(3, splicer.DroppedBytes);
    }

    // ---- an independent encoder/decoder for the fields under test ----

    private static byte[] Packet(int pid, bool pusi, long? pcr = null, long? pts = null, long? dts = null)
    {
        var p = new byte[TsSplicer.PacketSize];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47;
        p[1] = (byte)((pusi ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        p[2] = (byte)pid;

        var i = 4;
        if (pcr is { } pcrBase)
        {
            p[3] = 0x30;          // adaptation field + payload, counter 0
            p[4] = 7;             // adaptation field length
            p[5] = 0x10;          // PCR present
            p[6] = (byte)(pcrBase >> 25);
            p[7] = (byte)(pcrBase >> 17);
            p[8] = (byte)(pcrBase >> 9);
            p[9] = (byte)(pcrBase >> 1);
            p[10] = (byte)(((pcrBase & 1) << 7) | 0x7E);
            p[11] = 0;
            i = 12;
        }
        else
        {
            p[3] = 0x10;          // payload only, counter 0
        }

        if (pts is { } presentation)
        {
            p[i] = 0;
            p[i + 1] = 0;
            p[i + 2] = 1;
            p[i + 3] = (byte)(pid == Audio ? 0xC0 : 0xE0);
            p[i + 4] = 0;
            p[i + 5] = 0;
            p[i + 6] = 0x80;
            p[i + 7] = (byte)(dts is null ? 0x80 : 0xC0);
            p[i + 8] = (byte)(dts is null ? 5 : 10);
            EncodeTimestamp(p, i + 9, dts is null ? 0x2 : 0x3, presentation);
            if (dts is { } decode)
            {
                EncodeTimestamp(p, i + 14, 0x1, decode);
            }
        }
        else if (pusi && pid != 0)
        {
            p[i] = 0x47; // not a PES start
        }

        return p;
    }

    private static void EncodeTimestamp(byte[] p, int i, int prefix, long ts)
    {
        p[i] = (byte)((prefix << 4) | (int)(((ts >> 30) & 0x7) << 1) | 1);
        p[i + 1] = (byte)((ts >> 22) & 0xFF);
        p[i + 2] = (byte)((((ts >> 15) & 0x7F) << 1) | 1);
        p[i + 3] = (byte)((ts >> 7) & 0xFF);
        p[i + 4] = (byte)(((ts & 0x7F) << 1) | 1);
    }

    private static int Pid(byte[] p) => ((p[1] & 0x1F) << 8) | p[2];

    private static int PayloadStart(byte[] p) => (p[3] & 0x20) != 0 ? 5 + p[4] : 4;

    private static long DecodeTimestamp(byte[] p, int i)
        => (((long)p[i] >> 1 & 0x7) << 30) | ((long)p[i + 1] << 22) | (((long)p[i + 2] >> 1) << 15) | ((long)p[i + 3] << 7) | ((long)p[i + 4] >> 1);

    private static bool HasPes(byte[] p)
    {
        var i = PayloadStart(p);
        return (p[1] & 0x40) != 0 && p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 1;
    }

    private static long Pts(byte[] p) => HasPes(p) ? DecodeTimestamp(p, PayloadStart(p) + 9) : -1;

    private static long Dts(byte[] p)
        => HasPes(p) && (p[PayloadStart(p) + 7] >> 6) == 3 ? DecodeTimestamp(p, PayloadStart(p) + 14) : Pts(p);

    // Decode order: DTS where present, otherwise PTS.
    private static long Decode(byte[] p) => Dts(p);

    private static long Pcr(byte[] p)
        => ((long)p[6] << 25) | ((long)p[7] << 17) | ((long)p[8] << 9) | ((long)p[9] << 1) | ((long)p[10] >> 7);
}
