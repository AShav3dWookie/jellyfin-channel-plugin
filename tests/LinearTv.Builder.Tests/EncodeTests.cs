using LinearTv.Builder.Encode;
using LinearTv.Builder.Library;

namespace LinearTv.Builder.Tests;

public class EncodeTests
{
    private static readonly VideoInfo Hd = new(0, "h264", 1920, 1080, 25, false, false, false);

    private static SourceItem Item(VideoInfo? video = null, MediaTrack[]? subtitles = null) => new()
    {
        Id = Guid.Parse("11111111111111111111111111111111"),
        Type = "Movie",
        Name = "Film",
        Video = video ?? Hd,
        AudioTracks = [new MediaTrack { Index = 1, Codec = "aac", Language = "eng" }],
        SubtitleTracks = subtitles ?? [],
    };

    private static string Arg(IReadOnlyList<string> args, string name) => args[args.ToList().IndexOf(name) + 1];

    // ---- frame rates ----

    [Theory]
    [InlineData(23.976, "24000/1001")]
    [InlineData(23.98, "24000/1001")]
    [InlineData(24, "24")]
    [InlineData(25, "25")]
    [InlineData(29.97, "30000/1001")]
    [InlineData(50, "25")]
    [InlineData(59.94, "30000/1001")]
    [InlineData(0, "25")]
    [InlineData(15, "25")]
    public void FrameRate_Nearest_SnapsToAStandardRateNoHigherThan30(double fps, string expected)
        => Assert.Equal(expected, FrameRate.Nearest(fps).ToString());

    [Fact]
    public void FrameRate_ForChannel_IsTheRateMostOfTheRunningTimeIsIn()
    {
        static SourceItem At(double fps, long hours) => new()
        {
            Id = Guid.NewGuid(), Type = "Movie", Name = "x", RuntimeTicks = TimeSpan.FromHours(hours).Ticks,
            Video = new VideoInfo(0, "h264", 1920, 1080, fps, false, false, false),
        };

        // Three 25 fps items, but more hours at 23.976.
        Assert.Equal(FrameRate.Film, FrameRate.ForChannel([At(25, 1), At(25, 1), At(25, 1), At(23.976, 5)]));
    }

    [Fact]
    public void FrameRate_GopIsTwoSeconds()
    {
        Assert.Equal(50, FrameRate.Pal.GopFrames);
        Assert.Equal(48, FrameRate.Film.GopFrames);
    }

    // ---- the command ----

    [Fact]
    public void Arguments_KeepTheShape_SquareThePixels_AndPadTo720()
    {
        var graph = Arg(EncodeProfile.Arguments(Item(), new TrackChoice(Item().AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts"), "-filter_complex");

        Assert.StartsWith("[0:0]fps=25,scale=w='trunc(iw*sar/2)*2':h=ih", graph);
        Assert.Contains("scale=w=1280:h=720:force_original_aspect_ratio=decrease", graph);
        Assert.Contains("pad=1280:720:-1:-1:color=black", graph);
        Assert.EndsWith("format=yuv420p[vout]", graph);
    }

    [Fact]
    public void Arguments_MapTheChosenAudio_AndUseSilenceWhenThereIsNone()
    {
        var item = Item();
        var withAudio = EncodeProfile.Arguments(item, new TrackChoice(item.AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts");
        var silent = EncodeProfile.Arguments(item, new TrackChoice(null, null), "in.mkv", null, FrameRate.Pal, "out.ts");

        Assert.Contains("0:1", withAudio);
        Assert.Contains("anullsrc=r=48000:cl=stereo", silent);
        Assert.Contains("1:a:0", silent);
        Assert.Equal("192k", Arg(withAudio, "-b:a"));
        Assert.Equal("48000", Arg(withAudio, "-ar"));
        Assert.Equal(("hevc_nvenc", "main", "30"), (Arg(withAudio, "-c:v"), Arg(withAudio, "-profile:v"), Arg(withAudio, "-cq")));
    }

    [Fact]
    public void Arguments_1080p_FitsTo1920x1080_WithTheHigherBitrateCeiling()
    {
        var item = Item(Hd with { Codec = "hevc", PixelFormat = "yuv420p" });
        var args = EncodeProfile.Arguments(item, new TrackChoice(item.AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts", size: Resolution.Hd1080);

        Assert.Contains("scale_cuda=w=1920:h=1080", Arg(args, "-filter_complex"));
        Assert.Contains("pad=1920:1080:-1:-1", Arg(args, "-filter_complex"));
        Assert.Equal(("20M", "40M", "30"), (Arg(args, "-maxrate"), Arg(args, "-bufsize"), Arg(args, "-cq")));
        Assert.Equal("26", Arg(EncodeProfile.Arguments(item, new TrackChoice(null, null), "in.mkv", null, FrameRate.Pal, "out.ts", size: Resolution.Hd1080 with { Cq = 26 }), "-cq"));
        Assert.NotEqual(
            EncodeProfile.CacheKey(item, new TrackChoice(null, null), FrameRate.Pal),
            EncodeProfile.CacheKey(item, new TrackChoice(null, null), FrameRate.Pal, size: Resolution.Hd1080));
    }

    [Fact]
    public void Arguments_EndBothStreamsOnTheLastWholeFrame_NotAtTheShorter()
    {
        // 5400.5 s at 25 fps is 135,012.5 frames: the cut is after frame 135,012.
        var item = Item() with { RuntimeTicks = TimeSpan.FromSeconds(5400.5).Ticks };
        var args = EncodeProfile.Arguments(item, new TrackChoice(item.AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts");

        Assert.Equal("5400.480", Arg(args, "-t"));
        Assert.EndsWith("asetpts=N/SR/TB,apad=whole_dur=5400.480", Arg(args, "-af"));
        Assert.DoesNotContain("-shortest", args);
    }

    [Fact]
    public void Arguments_PlainSdr_IsDecodedAndScaledOnTheGpu()
    {
        var item = Item(Hd with { Codec = "hevc", PixelFormat = "yuv420p10le" });
        var args = EncodeProfile.Arguments(item, new TrackChoice(item.AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts");

        Assert.Equal("cuda", Arg(args, "-hwaccel_output_format"));
        Assert.StartsWith("[0:0]scale_cuda=w=1280:h=720:force_original_aspect_ratio=decrease", Arg(args, "-filter_complex"));
        Assert.Contains("hwdownload,format=nv12,fps=25,pad=1280:720", Arg(args, "-filter_complex"));
    }

    [Theory]
    [InlineData("interlaced")]
    [InlineData("anamorphic")]
    [InlineData("hdr")]
    [InlineData("xvid")]
    [InlineData("image subtitles")]
    [InlineData("gpu frames off")]
    public void Arguments_SourcesTheGpuPathCantTake_UseTheCpuChain(string why)
    {
        var video = (Hd with { Codec = "h264", PixelFormat = "yuv420p" }) switch
        {
            var v when why == "interlaced" => v with { Interlaced = true },
            var v when why == "anamorphic" => v with { Anamorphic = true },
            var v when why == "hdr" => v with { Hdr = true },
            var v when why == "xvid" => v with { Codec = "mpeg4" },
            var v => v,
        };
        var pgs = new MediaTrack { Index = 3, Codec = "hdmv_pgs_subtitle", Language = "eng" };
        var item = Item(video, [pgs]);
        var choice = new TrackChoice(null, why == "image subtitles" ? pgs : null);

        var args = EncodeProfile.Arguments(item, choice, "in.mkv", null, FrameRate.Pal, "out.ts", gpuFrames: why != "gpu frames off");

        Assert.DoesNotContain("-hwaccel_output_format", args);
        Assert.DoesNotContain("scale_cuda", Arg(args, "-filter_complex"));
    }

    [Fact]
    public void Arguments_DownmixToStereo_BeforeNormalisingLoudness()
        => Assert.StartsWith(
            "aresample=48000,aformat=channel_layouts=stereo,loudnorm=",
            Arg(EncodeProfile.Arguments(Item(), new TrackChoice(Item().AudioTracks[0], null), "in.mkv", null, FrameRate.Pal, "out.ts"), "-af"));

    [Fact]
    public void Arguments_Hdr_IsToneMappedAndFittedOnTheGpu_AfterDeinterlacing()
    {
        var item = Item(Hd with { Interlaced = true, Hdr = true });
        var graph = Arg(EncodeProfile.Arguments(item, new TrackChoice(null, null), "in.mkv", null, FrameRate.Pal, "out.ts"), "-filter_complex");

        Assert.StartsWith("[0:0]bwdif=mode=send_frame,libplacebo=w=1280:h=720:force_original_aspect_ratio=decrease", graph);
        Assert.Contains("normalize_sar=1:tonemapping=bt.2390", graph);
        Assert.Contains("pad=1280:720:-1:-1", graph);
        Assert.DoesNotContain("zscale", graph);
    }

    [Fact]
    public void Arguments_HdrWithImageSubtitles_ToneMapsBeforeTheOverlay_AndScalesAfter()
    {
        var pgs = new MediaTrack { Index = 3, Codec = "hdmv_pgs_subtitle", Language = "eng" };
        var item = Item(Hd with { Hdr = true }, [pgs]);
        var graph = Arg(EncodeProfile.Arguments(item, new TrackChoice(null, pgs), "in.mkv", null, FrameRate.Pal, "out.ts"), "-filter_complex");

        Assert.StartsWith("[0:0]libplacebo=tonemapping=bt.2390", graph);
        Assert.Contains("[base][sub]overlay=eof_action=pass,fps=25,scale=w='trunc(iw*sar/2)*2'", graph);
    }

    [Fact]
    public void Arguments_ImageSubtitles_AreOverlaidAtSourceResolution()
    {
        var pgs = new MediaTrack { Index = 3, Codec = "hdmv_pgs_subtitle", Language = "eng" };
        var item = Item(subtitles: [pgs]);
        var graph = Arg(EncodeProfile.Arguments(item, new TrackChoice(null, pgs), "in.mkv", null, FrameRate.Pal, "out.ts"), "-filter_complex");

        Assert.StartsWith("[0:0]null[base];[0:3]scale=1920:1080[sub];[base][sub]overlay=eof_action=pass,fps=25", graph);
    }

    [Fact]
    public void Arguments_ExternalImageSubtitles_AreAnInputOfTheirOwn_BeforeTheSilence()
    {
        var sup = new MediaTrack { Index = 9, Codec = "hdmv_pgs_subtitle", Language = "eng", ExternalPath = "/media/a.sup" };
        var args = EncodeProfile.Arguments(Item(subtitles: [sup]), new TrackChoice(null, sup), "in.mkv", @"D:\a.sup", FrameRate.Pal, "out.ts");

        Assert.Equal(["in.mkv", @"D:\a.sup", "anullsrc=r=48000:cl=stereo"], args.Select((a, i) => (a, i)).Where(x => x.i > 0 && args[x.i - 1] == "-i").Select(x => x.a));
        Assert.Contains("[1:0]scale=", Arg(args, "-filter_complex"));
        Assert.Contains("2:a:0", args);
    }

    [Fact]
    public void Arguments_TextSubtitles_AreRenderedOntoTheFinalFrame_ByPositionAmongTheFilesSubtitles()
    {
        var first = new MediaTrack { Index = 3, Codec = "subrip", Language = "fra", IsText = true };
        var chosen = new MediaTrack { Index = 5, Codec = "subrip", Language = "eng", IsText = true };
        var external = new MediaTrack { Index = 7, Codec = "subrip", Language = "eng", IsText = true, ExternalPath = "/x.srt" };
        var item = Item(subtitles: [external, chosen, first]);

        var graph = Arg(EncodeProfile.Arguments(item, new TrackChoice(null, chosen), "C:/m/in.mkv", null, FrameRate.Pal, "out.ts"), "-filter_complex");

        Assert.Contains(@"setsar=1,subtitles=filename=C\\:/m/in.mkv:si=1,format=yuv420p[vout]", graph);
    }

    [Theory]
    [InlineData(@"D:\Films\a.mkv", @"D\\:/Films/a.mkv")]
    [InlineData(@"\\TRUENAS\media\Schindler's List (1993).mkv", @"//TRUENAS/media/Schindler\\\'s List (1993).mkv")]
    [InlineData("/m/[2001] a, b; c.mkv", @"/m/\[2001\] a\, b\; c.mkv")]
    public void FilterValue_EscapesForBothParsers(string path, string expected)
        => Assert.Equal(expected, EncodeProfile.FilterValue(path));

    [Fact]
    public void CacheKey_ChangesWithAnythingThatChangesTheOutput()
    {
        var item = Item();
        var audio = new TrackChoice(item.AudioTracks[0], null);
        var key = EncodeProfile.CacheKey(item, audio, FrameRate.Pal);

        Assert.Equal(key, EncodeProfile.CacheKey(item, audio, FrameRate.Pal));
        Assert.NotEqual(key, EncodeProfile.CacheKey(item, audio, FrameRate.Film));
        Assert.NotEqual(key, EncodeProfile.CacheKey(item, new TrackChoice(null, null), FrameRate.Pal));
        Assert.StartsWith("11111111111111111111111111111111-", key);
        Assert.NotEqual(key, EncodeProfile.CacheKey(item, audio, FrameRate.Pal, "p7"));
    }

    [Fact]
    public void Arguments_UseThePresetAsked_AndRefuseOthers()
    {
        var item = Item();
        var choice = new TrackChoice(item.AudioTracks[0], null);

        Assert.Equal("p4", Arg(EncodeProfile.Arguments(item, choice, "in.mkv", null, FrameRate.Pal, "out.ts"), "-preset"));
        Assert.Equal("p7", Arg(EncodeProfile.Arguments(item, choice, "in.mkv", null, FrameRate.Pal, "out.ts", "p7"), "-preset"));
        Assert.Throws<ArgumentException>(() => EncodeProfile.Arguments(item, choice, "in.mkv", null, FrameRate.Pal, "out.ts", "slow"));
    }

    // ---- lineup ----

    [Fact]
    public void Lineup_InOrder_RepeatsTheListPerPass()
        => Assert.Equal([1, 2, 3, 1, 2, 3], Lineup.Build([1, 2, 3], shuffle: false, passes: 2, "201"));

    [Fact]
    public void Lineup_Shuffled_IsStable_AndEachPassIsAPermutation()
    {
        var items = Enumerable.Range(1, 20).ToList();
        var a = Lineup.Build(items, shuffle: true, passes: 3, "201");

        Assert.Equal(a, Lineup.Build(items, shuffle: true, passes: 3, "201"));
        Assert.NotEqual(a, Lineup.Build(items, shuffle: true, passes: 3, "202"));
        for (var p = 0; p < 3; p++)
        {
            Assert.Equal(items, a.Skip(p * 20).Take(20).Order());
        }
    }

    [Fact]
    public void Lineup_Shuffled_NeverPlaysAnItemTwiceInARow_IncludingAcrossTheLoop()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var lineup = Lineup.Build([1, 2, 3], shuffle: true, passes: 5, $"ch{seed}");
            for (var i = 0; i < lineup.Count; i++)
            {
                Assert.NotEqual(lineup[i], lineup[(i + 1) % lineup.Count]);
            }
        }
    }
}
