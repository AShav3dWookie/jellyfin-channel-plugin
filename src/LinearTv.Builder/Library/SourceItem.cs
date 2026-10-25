using System.Globalization;
using System.Text.Json;

namespace LinearTv.Builder.Library;

/// <summary>The video stream a channel encodes from an item.</summary>
internal sealed record VideoInfo(
    int Index, string Codec, int Width, int Height, double FrameRate, bool Interlaced, bool Hdr, bool DolbyVisionOnly,
    string? PixelFormat = null, bool Anamorphic = false);

/// <summary>An audio or subtitle track of an item, as Jellyfin probed it.</summary>
internal sealed record MediaTrack
{
    /// <summary>
    /// Gets the stream's index in the media file (ffmpeg's numbering). Jellyfin numbers external
    /// subtitle files on after the file's own streams; those are read as their file's only stream.
    /// </summary>
    public required int Index { get; init; }

    public required string Codec { get; init; }

    /// <summary>Gets the language, canonical (<see cref="Languages.Normalize"/>); null when untagged.</summary>
    public string? Language { get; init; }

    public string? Title { get; init; }

    public int Channels { get; init; }

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    public bool IsHearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether the file flags this as the original-language track (Matroska's FlagOriginal).</summary>
    public bool IsOriginal { get; init; }

    /// <summary>Gets a value indicating whether this is a text subtitle (SRT, ASS...), rather than an image one (PGS, VobSub).</summary>
    public bool IsText { get; init; }

    /// <summary>Gets the path of an external subtitle file, as the server sees it; null for a track inside the media file.</summary>
    public string? ExternalPath { get; init; }

    public override string ToString()
        => $"#{Index} {Languages.Name(Language)} {Codec}{(Title is null ? string.Empty : $" '{Title}'")}{(IsForced ? " forced" : string.Empty)}{(ExternalPath is null ? string.Empty : " (external)")}";
}

/// <summary>
/// One playable library item, as the builder needs it: where the file is, its streams, and what
/// the guide should say. Read from Jellyfin's <c>/Items</c> API.
/// </summary>
internal sealed record SourceItem
{
    public required Guid Id { get; init; }

    /// <summary>Gets Jellyfin's item type: Movie, Episode, Video, MusicVideo.</summary>
    public required string Type { get; init; }

    public required string Name { get; init; }

    public string? SeriesName { get; init; }

    public Guid? SeriesId { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public string? Rating { get; init; }

    public string? TmdbId { get; init; }

    public IReadOnlyList<string> ProductionCountries { get; init; } = [];

    /// <summary>Gets the file's path as the Jellyfin server sees it.</summary>
    public string? Path { get; init; }

    public long RuntimeTicks { get; init; }

    public VideoInfo? Video { get; init; }

    public IReadOnlyList<MediaTrack> AudioTracks { get; init; } = [];

    public IReadOnlyList<MediaTrack> SubtitleTracks { get; init; } = [];

    /// <summary>Gets the item whose primary image represents this one: itself, else its series.</summary>
    public string? ImageItemId { get; init; }

    public string? ImageTag { get; init; }

    public bool IsEpisode => Type == "Episode";

    public override string ToString() => IsEpisode
        ? $"{SeriesName} S{Season:00}E{Episode:00}"
        : $"{Name}{(Year is { } y ? $" ({y})" : string.Empty)}";

    /// <summary>The fields to ask <c>/Items</c> for, beyond its defaults.</summary>
    public const string Fields = "Path,MediaSources,MediaStreams,Overview,Genres,OfficialRating,ProductionYear,ProviderIds,ProductionLocations";

    /// <summary>Reads one entry of an <c>/Items</c> response.</summary>
    public static SourceItem FromJson(JsonElement item)
    {
        var source = item.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0
            ? sources[0]
            : (JsonElement?)null;
        var streams = source is { } s && s.TryGetProperty("MediaStreams", out var ms) && ms.ValueKind == JsonValueKind.Array
            ? ms.EnumerateArray().ToList()
            : item.TryGetProperty("MediaStreams", out var own) && own.ValueKind == JsonValueKind.Array ? own.EnumerateArray().ToList() : [];

        // Jellyfin numbers external subtitle files in with the file's own streams (first, in 12.1),
        // which shifts the rest. ffmpeg numbers only the file's own: take the externals out.
        var external = streams.Where(t => Bool(t, "IsExternal")).Select(t => Int(t, "Index") ?? 0).ToList();
        int FileIndex(JsonElement t) => (Int(t, "Index") ?? 0) is var index ? index - external.Count(e => e < index) : 0;

        var (imageItem, imageTag) = PrimaryImage(item);
        return new SourceItem
        {
            Id = Guid.Parse(Str(item, "Id")!),
            Type = Str(item, "Type") ?? "Video",
            Name = Str(item, "Name") ?? "Untitled",
            SeriesName = Str(item, "SeriesName"),
            SeriesId = Str(item, "SeriesId") is { } seriesId ? Guid.Parse(seriesId) : null,
            Season = Int(item, "ParentIndexNumber"),
            Episode = Int(item, "IndexNumber"),
            Year = Int(item, "ProductionYear"),
            Overview = Str(item, "Overview"),
            Genres = Strings(item, "Genres"),
            Rating = Str(item, "OfficialRating"),
            TmdbId = TmdbIdOf(item),
            ProductionCountries = Strings(item, "ProductionLocations"),
            Path = (source is { } src ? Str(src, "Path") : null) ?? Str(item, "Path"),
            RuntimeTicks = Long(item, "RunTimeTicks") ?? (source is { } r ? Long(r, "RunTimeTicks") : null) ?? 0,
            Video = ReadVideo(streams, FileIndex),
            AudioTracks = streams.Where(t => Str(t, "Type") == "Audio" && !Bool(t, "IsExternal")).Select(t => ReadTrack(t, FileIndex)).ToList(),
            SubtitleTracks = streams.Where(t => Str(t, "Type") == "Subtitle").Select(t => ReadTrack(t, FileIndex)).ToList(),
            ImageItemId = imageItem,
            ImageTag = imageTag,
        };
    }

    /// <summary>A series' or film's TMDb id from an <c>/Items</c> entry, if Jellyfin matched one.</summary>
    public static string? TmdbIdOf(JsonElement item)
        => item.TryGetProperty("ProviderIds", out var ids) && ids.ValueKind == JsonValueKind.Object ? Str(ids, "Tmdb") : null;

    /// <summary>A JSON string array property, or empty.</summary>
    public static List<string> Strings(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
            : [];

    private static VideoInfo? ReadVideo(List<JsonElement> streams, Func<JsonElement, int> fileIndex)
    {
        // Cover art embedded in a file shows up as a "video" stream too.
        var video = streams.FirstOrDefault(s => Str(s, "Type") == "Video" && !Bool(s, "IsExternal") && Str(s, "Codec") is not ("mjpeg" or "png"));
        if (video.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var transfer = Str(video, "ColorTransfer");
        var hdr = Str(video, "VideoRange") == "HDR" || transfer is "smpte2084" or "arib-std-b67";
        return new VideoInfo(
            fileIndex(video),
            Str(video, "Codec") ?? "unknown",
            Int(video, "Width") ?? 0,
            Int(video, "Height") ?? 0,
            Double(video, "RealFrameRate") ?? Double(video, "AverageFrameRate") ?? 0,
            Bool(video, "IsInterlaced"),
            hdr,
            // Profile 5 has no HDR10 base layer: without Dolby's reshaping its colours are wrong.
            Str(video, "VideoRangeType") == "DOVI",
            Str(video, "PixelFormat"),
            Bool(video, "IsAnamorphic"));
    }

    private static MediaTrack ReadTrack(JsonElement t, Func<JsonElement, int> fileIndex) => new()
    {
        // An external file is read as its own input: its index in the media file means nothing.
        Index = Bool(t, "IsExternal") ? Int(t, "Index") ?? 0 : fileIndex(t),
        Codec = Str(t, "Codec") ?? "unknown",
        Language = Languages.Normalize(Str(t, "Language")),
        Title = Str(t, "Title"),
        Channels = Int(t, "Channels") ?? 0,
        IsDefault = Bool(t, "IsDefault"),
        IsForced = Bool(t, "IsForced"),
        IsHearingImpaired = Bool(t, "IsHearingImpaired"),
        IsOriginal = Bool(t, "IsOriginal"),
        IsText = Bool(t, "IsTextSubtitleStream"),
        ExternalPath = Bool(t, "IsExternal") ? Str(t, "Path") : null,
    };

    private static (string? Item, string? Tag) PrimaryImage(JsonElement item)
    {
        if (item.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("Primary", out var primary) && primary.GetString() is { } tag)
        {
            return (Str(item, "Id"), tag);
        }

        // Episodes often have no image of their own; the series poster stands in.
        return Str(item, "SeriesPrimaryImageTag") is { } seriesTag && Str(item, "SeriesId") is { } seriesId
            ? (seriesId, seriesTag)
            : (null, null);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? Double(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String
              && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
