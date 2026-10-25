using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LinearTv.Store;
using LinearTv.Builder.Library;

namespace LinearTv.Builder.Encode;

/// <summary>
/// The one format every channel is encoded to, and the ffmpeg command that produces it from any
/// source. docs/architecture.md, "Fixed format", explains the choices.
/// </summary>
/// <remarks>
/// Everything that affects the output is in <see cref="Arguments"/>, and the piece key hashes it,
/// so a change here re-encodes rather than mixing old and new pieces in one channel.
/// </remarks>
internal static class EncodeProfile
{
    public const int AudioBitRate = 192_000;
    public const int SampleRate = 48_000;

    /// <summary>
    /// The NVENC preset unless asked otherwise. Measured for H.265 at CQ30 on the A5000: p4 runs
    /// 34x real time against p7's 13x, for files about 2% bigger and 0.2-0.5 VMAF lower. At p7 the
    /// GPU encoder, shared by every encode at once, was the build's bottleneck.
    /// </summary>
    public const string DefaultPreset = "p4";

    /// <summary>Raise when something changes that <see cref="Arguments"/> doesn't show, such as the ffmpeg build.</summary>
    private const int Revision = 1;

    public static VideoFormat Video(FrameRate rate, Resolution size) => new()
    {
        Codec = "hevc", Profile = "Main", Level = 123, Width = size.Width, Height = size.Height,
        FrameRate = Math.Round(rate.Value, 3), MaxBitRate = int.Parse(size.MaxRate.TrimEnd('M'), CultureInfo.InvariantCulture) * 1_000_000,
    };

    public static AudioFormat Audio() => new() { Codec = "aac", Channels = 2, SampleRate = SampleRate, BitRate = AudioBitRate };

    /// <summary>The ffmpeg arguments that encode an item, with its chosen tracks, from <paramref name="input"/> to a piece.</summary>
    /// <param name="subtitleInput">The local path of an external subtitle file, when <paramref name="choice"/> burns one in.</param>
    /// <param name="preset">NVENC preset, p1 (fastest) to p7 (best quality per bit).</param>
    /// <param name="gpuFrames">Keep decoded frames on the GPU and scale there, where the source allows
    /// (<see cref="StaysOnGpu"/>). False forces the CPU chain: the fallback if the GPU path fails.</param>
    public static IReadOnlyList<string> Arguments(
        SourceItem item, TrackChoice choice, string input, string? subtitleInput, FrameRate rate, string output,
        string preset = DefaultPreset, bool hardwareDecode = true, bool gpuFrames = true, Resolution? size = null)
    {
        size ??= Resolution.Default;
        if (!IsPreset(preset))
        {
            throw new ArgumentException($"NVENC presets are p1 to p7, not '{preset}'.", nameof(preset));
        }

        var video = item.Video ?? throw new ArgumentException($"{item} has no video stream.", nameof(item));
        var subtitle = choice.Subtitle;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };

        // Decoding on the GPU frees the CPU for filtering and audio. ffmpeg falls back to
        // software for anything the GPU can't decode (XviD, for one). Where the source allows, the
        // frames stay on the GPU to be scaled there too.
        var onGpu = hardwareDecode && gpuFrames && StaysOnGpu(video, choice);
        if (hardwareDecode)
        {
            args.AddRange(["-hwaccel", "cuda"]);
        }

        if (onGpu)
        {
            args.AddRange(["-hwaccel_output_format", "cuda"]);
        }

        args.AddRange(["-i", input]);
        var nextInput = 1;
        string? imageSubtitle = null;
        if (subtitle is { IsText: false })
        {
            if (subtitle.ExternalPath is not null)
            {
                args.AddRange(["-i", RequireLocal(subtitleInput)]);
                imageSubtitle = $"{nextInput++}:0";
            }
            else
            {
                imageSubtitle = $"0:{subtitle.Index}";
            }
        }

        var audioRef = choice.Audio is { } a ? $"0:{a.Index}" : $"{nextInput}:a:0";
        if (choice.Audio is null)
        {
            args.AddRange(["-f", "lavfi", "-i", $"anullsrc=r={SampleRate}:cl=stereo"]);
        }

        string? textSubtitle = null;
        if (subtitle is { IsText: true })
        {
            textSubtitle = subtitle.ExternalPath is not null
                ? $"subtitles=filename={FilterValue(RequireLocal(subtitleInput))}"
                : $"subtitles=filename={FilterValue(input)}:si={SubtitlePosition(item, subtitle)}";
        }

        args.AddRange(["-filter_complex", VideoGraph(video, rate, imageSubtitle, textSubtitle, onGpu, size), "-map", "[vout]", "-map", audioRef]);
        args.AddRange(
        [
            // H.265 at constant quality 30: measured on Akira and Parasite, 0.4-0.6 GB per hour at
            // VMAF 93-95, against 1.5-2.5 GB per hour for H.264 at CQ21 (VMAF 96-98). Chosen for
            // size; TV apps direct-play it, browsers vary (docs/architecture.md).
            "-c:v", "hevc_nvenc", "-preset", preset, "-tune", "hq", "-profile:v", "main", "-level:v", "4.1", "-tier", "main",
            "-rc", "vbr", "-cq", size.Cq.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-maxrate", size.MaxRate, "-bufsize", size.BufferSize,
            // A keyframe every 2 s exactly, each an IDR with SPS/PPS: tuning in can start at any one.
            "-g", rate.GopFrames.ToString(CultureInfo.InvariantCulture), "-strict_gop", "1", "-no-scenecut", "1", "-forced-idr", "1",
            "-bf", "2", "-spatial-aq", "1", "-temporal-aq", "1",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
        ]);
        // Both streams end at the item's running time: audio padded with silence if it's short,
        // and anything past the end cut. Not -shortest, which lets audio run up to seconds past
        // the picture when the video encode is slower (tone-mapping), and the next programme
        // would then start after a gap.
        // Cut on the last whole frame, so the audio ends with the picture rather than a fraction of
        // a frame after it.
        var frames = Math.Floor((TimeSpan.FromTicks(item.RuntimeTicks).TotalSeconds * rate.Value) + 0.01);
        var duration = (frames / rate.Value).ToString("0.000", CultureInfo.InvariantCulture);
        args.AddRange(
        [
            // Downmixed to stereo first, then loudness-normalised so programmes match in volume.
            // In that order loudnorm (which works at 192 kHz) has two channels to process, not six:
            // a third of the CPU on a 5.1 source, for the same result (measured: -22.6 LUFS
            // either way, peak -4.8 vs -5.0 dBFS). loudnorm leaves an ~90 ms gap in its timestamps
            // near the end (measured), heard as a dropout: asetpts renumbers the samples after it.
            "-af", $"aresample={SampleRate},aformat=channel_layouts=stereo,loudnorm=I=-23:LRA=11:TP=-2,aresample={SampleRate},asetpts=N/SR/TB,apad=whole_dur={duration}",
            "-ac", "2", "-c:a", "aac", "-b:a", "192k", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
        ]);
        args.AddRange(
        [
            "-t", duration, "-fps_mode", "cfr", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            // Fixed PIDs and no mux delay: every piece has the same layout, starting at time 0.
            "-f", "mpegts", "-mpegts_service_id", "1", "-mpegts_pmt_start_pid", "4096", "-mpegts_start_pid", "256",
            "-muxdelay", "0", "-muxpreload", "0",
            output,
        ]);
        return args;
    }

    /// <summary>
    /// The video filter graph, from the source's video stream to <c>[vout]</c>: deinterlace,
    /// tone-map, overlay image subtitles at the source's resolution, convert the frame rate,
    /// square the pixels, fit inside 1280×720 without changing the shape, pad with black, then
    /// render text subtitles onto the final frame.
    /// </summary>
    internal static string VideoGraph(
        VideoInfo video, FrameRate rate, string? imageSubtitle, string? textSubtitle, bool onGpu = false, Resolution? size = null)
    {
        size ??= Resolution.Default;
        var (Width, Height) = (size.Width, size.Height);
        var source = $"[0:{video.Index}]";

        // Frames decoded on the GPU are scaled there and only the 720p result comes back: measured
        // on a 1080p Simpsons episode, 19 CPU-seconds per 3 minutes against 37 for the CPU chain.
        if (onGpu)
        {
            var chain = new List<string>
            {
                $"scale_cuda=w={Width}:h={Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:format=nv12",
                "hwdownload",
                "format=nv12",
                $"fps={rate}",
                $"pad={Width}:{Height}:-1:-1:color=black",
                "setsar=1",
            };
            if (textSubtitle is not null)
            {
                chain.Add(textSubtitle);
            }

            chain.Add("format=yuv420p");
            return $"{source}{string.Join(',', chain)}[vout]";
        }

        var before = new List<string>();
        if (video.Interlaced)
        {
            before.Add("bwdif=mode=send_frame");
        }

        // HDR is tone-mapped on the GPU by libplacebo (Vulkan): measured on Psycho, 12.7x real time
        // tone-mapping and scaling there, 10.6x tone-mapping only, against 6.8x for zscale+tonemap
        // on the CPU. Without image subtitles it scales as well, to the fitted size; with them, the
        // subtitles go on after tone-mapping and before scaling, so it only tone-maps.
        const string ToneMap = "tonemapping=bt.2390:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format=yuv420p";
        if (video.Hdr && imageSubtitle is null)
        {
            var gpu = new List<string>(before)
            {
                $"libplacebo=w={Width}:h={Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:normalize_sar=1:{ToneMap}",
                $"fps={rate}",
                $"pad={Width}:{Height}:-1:-1:color=black",
                "setsar=1",
            };
            if (textSubtitle is not null)
            {
                gpu.Add(textSubtitle);
            }

            gpu.Add("format=yuv420p");
            return $"{source}{string.Join(',', gpu)}[vout]";
        }

        if (video.Hdr)
        {
            before.Add($"libplacebo={ToneMap}");
        }

        var after = new List<string>
        {
            $"fps={rate}",
            // Anamorphic sources (DVD's 720x576 shown as 16:9) are stretched to their display
            // shape first; for square pixels this changes nothing.
            "scale=w='trunc(iw*sar/2)*2':h=ih:flags=lanczos",
            "setsar=1",
            $"scale=w={Width}:h={Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos",
            $"pad={Width}:{Height}:-1:-1:color=black",
            "setsar=1",
        };
        if (textSubtitle is not null)
        {
            after.Add(textSubtitle);
        }

        after.Add("format=yuv420p");

        if (imageSubtitle is null)
        {
            return $"{source}{string.Join(',', before.Concat(after))}[vout]";
        }

        // Subtitle bitmaps are drawn for a canvas that can differ from the video (1080p subtitles
        // on a 2160p film), so they're scaled to the video first.
        var pre = before.Count == 0 ? "null" : string.Join(',', before);
        return $"{source}{pre}[base];[{imageSubtitle}]scale={video.Width}:{video.Height}[sub];"
            + $"[base][sub]overlay=eof_action=pass,{string.Join(',', after)}[vout]";
    }

    /// <summary>
    /// A path as a filter option value inside a filter graph: escaped once for the option parser
    /// (quote, colon, backslash) and once more for the graph parser (quote, backslash, brackets,
    /// comma, semicolon). Forward slashes work on Windows and need no escaping.
    /// </summary>
    internal static string FilterValue(string path)
        => Escape(Escape(path.Replace('\\', '/'), @"\':"), @"\'[],;");

    /// <summary>
    /// The piece file name for an item: the item, plus a hash of everything that shapes the
    /// output except the paths. A piece left by an interrupted build is reused only if it matches.
    /// </summary>
    public static string CacheKey(SourceItem item, TrackChoice choice, FrameRate rate, string preset = DefaultPreset, Resolution? size = null)
    {
        var shape = string.Join(' ', Arguments(item, choice, "<in>", "<sub>", rate, "<out>", preset, size: size)) + $" r{Revision}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12];
        return $"{item.Id:N}-{hash}";
    }

    /// <summary>
    /// Whether an item can be decoded and scaled entirely on the GPU: progressive, square-pixel
    /// SDR video in a codec and pixel format NVDEC handles, with no image subtitles (those are
    /// overlaid at the source's resolution, on the CPU). Everything else takes the CPU chain.
    /// </summary>
    internal static bool StaysOnGpu(VideoInfo video, TrackChoice choice)
        => !video.Hdr && !video.Interlaced && !video.Anamorphic
            && choice.Subtitle is not { IsText: false }
            && video.Codec.ToLowerInvariant() is "h264" or "hevc" or "av1" or "vp9" or "mpeg2video"
            && video.PixelFormat is "yuv420p" or "yuv420p10le" or "yuvj420p" or "nv12" or "p010le";

    /// <summary>Whether <paramref name="preset"/> is an NVENC preset: p1 to p7.</summary>
    public static bool IsPreset(string preset) => preset is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7";

    /// <summary>A subtitle's position among the media file's own subtitle streams: what the subtitles filter's <c>si</c> counts.</summary>
    private static int SubtitlePosition(SourceItem item, MediaTrack subtitle)
        => item.SubtitleTracks.Where(t => t.ExternalPath is null).OrderBy(t => t.Index).TakeWhile(t => t.Index != subtitle.Index).Count();

    private static string RequireLocal(string? subtitleInput)
        => subtitleInput ?? throw new ArgumentException("An external subtitle needs its local path.", nameof(subtitleInput));

    private static string Escape(string value, string special)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (special.Contains(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
