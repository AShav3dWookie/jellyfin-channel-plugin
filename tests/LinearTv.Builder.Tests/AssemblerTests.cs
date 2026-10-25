using Jellyfin.Plugin.LinearTv.Store;
using LinearTv.Builder.Assemble;

namespace LinearTv.Builder.Tests;

public sealed class AssemblerTests : IDisposable
{
    private const int VideoPid = 0x100;
    private const int AudioPid = 0x101;
    private const int PmtPid = 0x1000;
    private const long Frame = 3600; // 40 ms at 90 kHz: 25 fps

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"assembler-{Guid.NewGuid():N}");

    public AssemblerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A piece as ffmpeg writes one: PAT, PMT, then frames, a keyframe every 50 (2 s).</summary>
    private string Piece(string name, int frames, long start = 0)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        file.Write(Pat());
        file.Write(Pmt());
        for (var i = 0; i < frames; i++)
        {
            var dts = start + (i * Frame);
            file.Write(Pes(VideoPid, keyframe: i % 50 == 0, pts: dts + 7200, dts: dts));
            file.Write(Pes(AudioPid, keyframe: false, pts: dts, dts: null));
        }

        return path;
    }

    private static ManifestProgramme Guide(string title) => new() { Title = title };

    [Fact]
    public void JoinsPieces_IntoOneContinuousTimeline_WithAnIndexEntryPerKeyframe()
    {
        // 3 s and 5 s pieces, both starting near 0 in their own time.
        var a = Piece("a.ts", 75);
        var b = Piece("b.ts", 125, start: 126000);

        var result = ChannelAssembler.Assemble([new(a, Guide("A")), new(b, Guide("B"))], _dir, chunked: false, frameRate: 25);

        Assert.Equal(["A", "B"], result.Programmes.Select(p => p.Title));
        Assert.Equal(0, result.Programmes[0].Start);

        // B follows A's last frame (2.96 s) after one frame's gap: TsSplicer's 40 ms.
        var bStart = TimeSpan.FromTicks(result.Programmes[1].Start);
        Assert.InRange(bStart.TotalMilliseconds, 2999, 3001);
        Assert.Equal(result.Programmes[1].Start, result.Programmes[0].Duration);

        // The loop: B's last frame at 3 + 4.96 s, plus a frame, plus the gap.
        Assert.InRange(TimeSpan.FromTicks(result.CycleTicks).TotalMilliseconds, 8039, 8041);
        Assert.Equal(result.CycleTicks, result.Programmes[1].End);

        var index = KeyframeIndex.ReadFile(Path.Combine(_dir, "index.bin"));
        Assert.Equal(5, index.Count); // A: 0, 2 s; B: 0, 2, 4 s
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, index.AtOrBefore(TimeSpan.FromSeconds(5.5).Ticks).Ticks);

        // Every index entry points at a video packet flagged as a keyframe.
        var bytes = File.ReadAllBytes(Path.Combine(_dir, "channel.ts"));
        for (var t = 0L; t < result.CycleTicks; t += TimeSpan.FromSeconds(1).Ticks)
        {
            var (_, offset) = index.AtOrBefore(t);
            var packet = bytes.AsSpan((int)offset, TsPacket.Size);
            Assert.Equal(VideoPid, TsPacket.Pid(packet));
            Assert.True(TsPacket.RandomAccess(packet));
        }

        Assert.Equal(bytes.Length, result.Files.Single().Bytes);
    }

    [Fact]
    public void ThePsi_IsTheFirstPatAndPmt()
    {
        var result = ChannelAssembler.Assemble([new(Piece("a.ts", 10), Guide("A"))], _dir, chunked: false, frameRate: 25);

        var psi = Convert.FromBase64String(result.Psi);
        Assert.Equal(2 * TsPacket.Size, psi.Length);
        Assert.Equal(PmtPid, TsPacket.PmtPid(psi.AsSpan(0, TsPacket.Size)));
        Assert.Equal([(0x1B, VideoPid), (0x0F, AudioPid)], TsPacket.PmtStreams(psi.AsSpan(TsPacket.Size, TsPacket.Size)));
    }

    [Fact]
    public void Chunked_CutsAtTheFirstKeyframePastEachChunkLength_AndOffsetsRunAcrossFiles()
    {
        var result = ChannelAssembler.Assemble(
            [new(Piece("a.ts", 250), Guide("A"))], _dir, chunked: true, frameRate: 25, chunkLength: TimeSpan.FromSeconds(3));

        // Keyframes at 0, 2, 4, 6, 8 s: chunks start at 0, 4 and 6 (first keyframes at or past 3 s and 6 s), then 10 is past the end.
        Assert.Equal(["channel-0000.ts", "channel-0001.ts", "channel-0002.ts"], result.Files.Select(f => f.Name));
        var index = KeyframeIndex.ReadFile(Path.Combine(_dir, "index.bin"));
        Assert.Equal(result.Files[0].Bytes, index.AtOrBefore(TimeSpan.FromSeconds(4).Ticks).Offset);
        Assert.Equal(result.Files[0].Bytes + result.Files[1].Bytes, index.AtOrBefore(TimeSpan.FromSeconds(6).Ticks).Offset);
    }

    [Fact]
    public void APieceMarkedForDeletion_IsDeletedOnceJoined_AndOthersAreKept()
    {
        var a = Piece("a.ts", 10);
        var b = Piece("b.ts", 10);

        ChannelAssembler.Assemble([new(a, Guide("A")), new(b, Guide("B"), DeleteAfter: true), new(a, Guide("A"), DeleteAfter: true)], _dir, false, 25);

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void APieceWithADifferentLayout_IsRefused()
    {
        var a = Piece("a.ts", 10);
        var other = Path.Combine(_dir, "other.ts");
        using (var file = File.Create(other))
        {
            file.Write(Pat());
            file.Write(Pmt(audioPid: 0x102));
            file.Write(Pes(VideoPid, keyframe: true, pts: 7200, dts: 0));
        }

        var error = Assert.Throws<InvalidDataException>(() => ChannelAssembler.Assemble([new(a, Guide("A")), new(other, Guide("B"))], _dir, false, 25));
        Assert.Contains("different stream layout", error.Message);
    }

    // ---- packets ----

    private static byte[] Pat()
    {
        var p = Blank(0, pusi: true);
        byte[] section = [0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xE0 | (PmtPid >> 8), PmtPid & 0xFF, 0, 0, 0, 0];
        p[4] = 0; // pointer field
        section.CopyTo(p, 5);
        return p;
    }

    private static byte[] Pmt(int audioPid = AudioPid)
    {
        var p = Blank(PmtPid, pusi: true);
        byte[] section =
        [
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00,
            0x1B, 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00,
            0x0F, (byte)(0xE0 | (audioPid >> 8)), (byte)(audioPid & 0xFF), 0xF0, 0x00,
            0, 0, 0, 0,
        ];
        p[4] = 0;
        section.CopyTo(p, 5);
        return p;
    }

    private static byte[] Pes(int pid, bool keyframe, long pts, long? dts)
    {
        var p = Blank(pid, pusi: true);
        var i = 4;
        if (keyframe)
        {
            p[3] = (byte)((p[3] & 0xCF) | 0x30); // adaptation + payload
            p[4] = 7;
            p[5] = 0x40 | 0x10;                  // random access, PCR
            WritePcr(p, 6, dts ?? pts);
            i = 12;
        }

        p[i] = 0; p[i + 1] = 0; p[i + 2] = 1; p[i + 3] = (byte)(pid == VideoPid ? 0xE0 : 0xC0);
        p[i + 6] = 0x80;
        p[i + 7] = (byte)(dts is null ? 0x80 : 0xC0);
        p[i + 8] = (byte)(dts is null ? 5 : 10);
        WriteTimestamp(p, i + 9, pts, dts is null ? 0x20 : 0x30);
        if (dts is { } d)
        {
            WriteTimestamp(p, i + 14, d, 0x10);
        }

        return p;
    }

    private static byte[] Blank(int pid, bool pusi)
    {
        var p = new byte[TsPacket.Size];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47;
        p[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8));
        p[2] = (byte)(pid & 0xFF);
        p[3] = 0x10;
        return p;
    }

    private static void WriteTimestamp(byte[] p, int i, long ts, int prefix)
    {
        p[i] = (byte)(prefix | (int)((ts >> 29) & 0x0E) | 1);
        p[i + 1] = (byte)(ts >> 22);
        p[i + 2] = (byte)((((ts >> 15) & 0x7F) << 1) | 1);
        p[i + 3] = (byte)(ts >> 7);
        p[i + 4] = (byte)(((ts & 0x7F) << 1) | 1);
    }

    private static void WritePcr(byte[] p, int i, long pcrBase)
    {
        p[i] = (byte)(pcrBase >> 25);
        p[i + 1] = (byte)(pcrBase >> 17);
        p[i + 2] = (byte)(pcrBase >> 9);
        p[i + 3] = (byte)(pcrBase >> 1);
        p[i + 4] = (byte)(((pcrBase & 1) << 7) | 0x7E);
        p[i + 5] = 0;
    }
}
