using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LinearTv.Store;

/// <summary>
/// A built channel's <c>channel.json</c>: what's in its stream file(s) and when. Written by the
/// builder on the PC, read by the plugin. Shared source: the builder links this file.
/// docs/architecture.md describes the format; keep the two in step.
/// </summary>
/// <remarks>
/// All times are 100 ns ticks into the channel file's own timeline, which starts at 0 with the
/// first programme's first video frame and runs continuously to <see cref="CycleTicks"/>.
/// </remarks>
internal sealed class ChannelManifest
{
    public const int CurrentFormat = 1;

    public const string FileName = "channel.json";

    public int Format { get; set; } = CurrentFormat;

    public string Number { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime Built { get; set; }

    /// <summary>Gets or sets the length of one loop of the channel, from its first frame to where the loop rejoins it.</summary>
    public long CycleTicks { get; set; }

    public VideoFormat Video { get; set; } = new();

    public AudioFormat Audio { get; set; } = new();

    /// <summary>Gets or sets the PAT and PMT packets, base64: sent first whenever a viewer joins mid-file.</summary>
    public string Psi { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream files, in order. Their bytes, end to end, are the channel.</summary>
    public List<ManifestFile> Files { get; set; } = [];

    /// <summary>Gets or sets the keyframe index file name (<see cref="KeyframeIndex"/>).</summary>
    public string Index { get; set; } = "index.bin";

    public List<ManifestProgramme> Programmes { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>The one video format every programme in a channel shares.</summary>
internal sealed class VideoFormat
{
    public string Codec { get; set; } = "hevc";

    public string Profile { get; set; } = "Main";

    /// <summary>Gets or sets the level as Jellyfin counts it: 41 for H.264 4.1, 123 for H.265 4.1 (general_level_idc).</summary>
    public int Level { get; set; } = 123;

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public double FrameRate { get; set; } = 25;

    public int MaxBitRate { get; set; } = 10_000_000;
}

/// <summary>The one audio format every programme in a channel shares.</summary>
internal sealed class AudioFormat
{
    public string Codec { get; set; } = "aac";

    public int Channels { get; set; } = 2;

    public int SampleRate { get; set; } = 48000;

    public int BitRate { get; set; } = 192_000;
}

/// <summary>One stream file of a channel.</summary>
internal sealed class ManifestFile
{
    public string Name { get; set; } = string.Empty;

    public long Bytes { get; set; }
}

/// <summary>One programme: where it is in the channel's timeline, and what the guide says about it.</summary>
internal sealed class ManifestProgramme
{
    public long Start { get; set; }

    public long Duration { get; set; }

    /// <summary>Gets or sets the film's title, or the series name for an episode.</summary>
    public string Title { get; set; } = string.Empty;

    public string? EpisodeTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public int? Year { get; set; }

    public bool Movie { get; set; }

    public string? Overview { get; set; }

    public List<string>? Genres { get; set; }

    public string? Rating { get; set; }

    /// <summary>Gets or sets the artwork, relative to the build folder, e.g. <c>art/…jpg</c>.</summary>
    public string? Image { get; set; }

    [JsonIgnore]
    public long End => Start + Duration;
}
