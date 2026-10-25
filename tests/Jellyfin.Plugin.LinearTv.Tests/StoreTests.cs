using System.Text.Json;
using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.LinearTv.Tests;

public sealed class StoreTests : IDisposable
{
    private const int Video = 0x100;
    private readonly string _store = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}");

    public StoreTests() => Directory.CreateDirectory(_store);

    public void Dispose() => Directory.Delete(_store, recursive: true);

    private string Build(string folder, string number, DateTime built, bool complete = true, int format = ChannelManifest.CurrentFormat)
    {
        var dir = Path.Combine(_store, folder);
        Directory.CreateDirectory(dir);
        if (complete)
        {
            var manifest = GuideTests.Channel(2, TimeSpan.FromMinutes(30));
            manifest.Number = number;
            manifest.Name = $"Channel {number}";
            manifest.Built = built;
            manifest.Format = format;
            File.WriteAllText(Path.Combine(dir, ChannelManifest.FileName), JsonSerializer.Serialize(manifest, ChannelManifest.JsonOptions));
        }

        return dir;
    }

    // ---- the store ----

    [Fact]
    public void Refresh_TakesTheNewestCompleteBuildOfEachChannel_InNumberOrder()
    {
        Build("201-20261001T000000Z", "201", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = Build("201-20261002T000000Z", "201", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        Build("201-20261003T000000Z", "201", default, complete: false); // still copying: no manifest yet
        Build("90-20261001T000000Z", "90", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Build("300-20261001T000000Z", "300", default, format: 99);     // a future format

        var channels = new ChannelStore(NullLogger<ChannelStore>.Instance).Refresh(_store);

        Assert.Equal(["90", "201"], channels.Select(c => c.Manifest.Number));
        Assert.Equal(newest, channels[1].Directory);
        Assert.Equal("ltv201", channels[1].Id);
    }

    [Fact]
    public void Refresh_KeepsAnUnchangedBuild_SoItsLoadedIndexIsKept()
    {
        Build("201-20261001T000000Z", "201", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var store = new ChannelStore(NullLogger<ChannelStore>.Instance);

        var first = store.Refresh(_store).Single();
        var second = store.Refresh(_store).Single();

        Assert.Same(first, second);
    }

    [Fact]
    public void Refresh_OfAMissingFolder_IsNoChannels()
        => Assert.Empty(new ChannelStore(NullLogger<ChannelStore>.Instance).Refresh(Path.Combine(_store, "nope")));

    [Fact]
    public void KeyframeIndex_RoundTrips_AndFindsTheKeyframeAtOrBefore()
    {
        using var buffer = new MemoryStream();
        KeyframeIndex.Write(buffer, [(0, 0), (20_000_000, 1880), (40_000_000, 3760)]);
        buffer.Position = 0;

        var index = KeyframeIndex.Read(buffer);

        Assert.Equal(3, index.Count);
        Assert.Equal((20_000_000L, 1880L), index.AtOrBefore(39_999_999));
        Assert.Equal((40_000_000L, 3760L), index.AtOrBefore(40_000_000));
        Assert.Equal((0L, 0L), index.AtOrBefore(-5));
    }

    // ---- the stream ----

    /// <summary>A file of video packets, one per 40 ms frame, PCR on each.</summary>
    private string Frames(string name, int count, long start = 0)
    {
        var path = Path.Combine(_store, name);
        using var file = File.Create(path);
        for (var i = 0; i < count; i++)
        {
            file.Write(VideoPacket(start + (i * 3600)));
        }

        return path;
    }

    [Fact]
    public async Task Stream_SendsThePsiFirst_ThenTheFileFromTheOffset()
    {
        var path = Frames("a.ts", 10);
        var psi = Psi();

        var packets = await Read(new StoreStream([(path, 10 * 188)], 4 * 188, psi, "test", NullLogger.Instance), 3);

        Assert.Equal(0, Pid(packets[0])); // PAT
        Assert.Equal(0x1000, Pid(packets[1])); // PMT
        Assert.Equal(4 * 3600, Dts(packets[2]));
    }

    [Fact]
    public async Task Stream_CrossesFromChunkToChunk_AndStartsInTheRightOne()
    {
        var a = Frames("c0.ts", 5);
        var b = Frames("c1.ts", 5, start: 5 * 3600);

        // Offset 7 packets across the files: the third packet of the second chunk.
        var packets = await Read(new StoreStream([(a, 5 * 188), (b, 5 * 188)], 7 * 188, Psi(), "test", NullLogger.Instance), 5);

        Assert.Equal([7 * 3600L, 8 * 3600L, 9 * 3600L], packets.Skip(2).Select(Dts));
    }

    [Fact]
    public async Task Stream_LoopsBackToTheStart_WithTimestampsRunningOn()
    {
        var path = Frames("a.ts", 5);

        var packets = await Read(new StoreStream([(path, 5 * 188)], 3 * 188, Psi(), "test", NullLogger.Instance), 2 + 2 + 5);

        // Frames 3 and 4, then the loop: frame 0 again, placed one frame (TsSplicer's gap) after frame 4.
        var dts = packets.Skip(2).Where(p => Pid(p) == Video).Select(Dts).ToList();
        Assert.Equal(3 * 3600, dts[0]);
        Assert.Equal(4 * 3600, dts[1]);
        Assert.Equal((4 * 3600) + TsSplicer.ProgrammeGap, dts[2]);
        Assert.True(dts.Zip(dts.Skip(1)).All(p => p.Second > p.First), "timestamps never go back");
    }

    private static async Task<List<byte[]>> Read(Stream stream, int packets)
    {
        await using (stream)
        {
            var result = new List<byte[]>();
            var buffer = new byte[188];
            while (result.Count < packets)
            {
                await stream.ReadExactlyAsync(buffer);
                result.Add(buffer.ToArray());
            }

            return result;
        }
    }

    private static byte[] Psi()
    {
        var pat = new byte[188];
        pat[0] = 0x47; pat[1] = 0x40; pat[3] = 0x10;
        var pmt = new byte[188];
        pmt[0] = 0x47; pmt[1] = 0x50; pmt[2] = 0x00; pmt[3] = 0x10;
        return [.. pat, .. pmt];
    }

    private static int Pid(byte[] p) => ((p[1] & 0x1F) << 8) | p[2];

    private static long Dts(byte[] p)
    {
        var i = 4 + 1 + p[4] + 14;
        return (long)((((ulong)p[i] >> 1 & 0x07UL) << 30) | ((ulong)p[i + 1] << 22) | (((ulong)p[i + 2] >> 1) << 15) | ((ulong)p[i + 3] << 7) | ((ulong)p[i + 4] >> 1));
    }

    /// <summary>A video PES start with PCR, PTS and DTS: enough for the splicer and pacer.</summary>
    private static byte[] VideoPacket(long dts)
    {
        var p = new byte[188];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47; p[1] = 0x40 | (Video >> 8); p[2] = Video & 0xFF; p[3] = 0x30;
        p[4] = 7; p[5] = 0x10;
        p[6] = (byte)(dts >> 25); p[7] = (byte)(dts >> 17); p[8] = (byte)(dts >> 9); p[9] = (byte)(dts >> 1); p[10] = (byte)(((dts & 1) << 7) | 0x7E); p[11] = 0;
        var i = 12;
        p[i] = 0; p[i + 1] = 0; p[i + 2] = 1; p[i + 3] = 0xE0; p[i + 4] = 0; p[i + 5] = 0;
        p[i + 6] = 0x80; p[i + 7] = 0xC0; p[i + 8] = 10;
        Timestamp(p, i + 9, dts + 7200, 0x30);
        Timestamp(p, i + 14, dts, 0x10);
        return p;
    }

    private static void Timestamp(byte[] p, int i, long ts, int prefix)
    {
        p[i] = (byte)(prefix | (int)((ts >> 29) & 0x0E) | 1);
        p[i + 1] = (byte)(ts >> 22);
        p[i + 2] = (byte)((((ts >> 15) & 0x7F) << 1) | 1);
        p[i + 3] = (byte)(ts >> 7);
        p[i + 4] = (byte)(((ts & 0x7F) << 1) | 1);
    }
}
