using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LinearTv.Import;

/// <summary>
/// The channel import/export format: a portable, human- and AI-writable description of channels.
/// Documented for users in docs/channel-format.md; keep the two in step.
/// </summary>
/// <remarks>
/// Content is referenced by name (title, year, series/season/episode) rather than by Jellyfin
/// item id, because ids are derived from file paths and differ between servers. An id may be
/// given as well; it's used when it exists on this server, and the name is used otherwise.
/// </remarks>
internal sealed class ChannelDocument
{
    public int Version { get; set; } = 1;

    public List<ChannelSpec> Channels { get; set; } = [];

    /// <summary>
    /// Lenient on the way in (any property casing, comments, trailing commas: hand-written and
    /// AI-written JSON both have them); camelCase and indented on the way out.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,

        // Served as application/json, never embedded in HTML, so apostrophes and accents can stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>One channel in a <see cref="ChannelDocument"/>.</summary>
internal sealed class ChannelSpec
{
    public string? Number { get; set; }

    public string? Name { get; set; }

    public bool Shuffle { get; set; }

    public List<ContentRef> Content { get; set; } = [];
}

/// <summary>
/// One source of content: a film, series, collection, library or single episode.
/// </summary>
internal sealed class ContentRef
{
    /// <summary>Gets or sets an optional Jellyfin item id, tried first.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets one of movie, series, collection, library, episode.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the title (movie, series, collection or library name).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets an optional year, to tell apart titles that share a name.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the series title, for an episode.</summary>
    public string? Series { get; set; }

    /// <summary>Gets or sets the season number, for an episode.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number, for an episode.</summary>
    public int? Episode { get; set; }

    /// <summary>A short description for reports, e.g. "movie 'Inception' (2010)".</summary>
    public override string ToString() => (Type?.ToLowerInvariant(), Id) switch
    {
        ("episode", _) => $"episode '{Series}' S{Season:00}E{Episode:00}",
        (_, { } id) when Title is null => $"id {id}",
        _ => $"{Type ?? "item"} '{Title}'{(Year is { } y ? $" ({y})" : string.Empty)}",
    };
}
