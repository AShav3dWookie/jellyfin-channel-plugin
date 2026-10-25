using Jellyfin.Plugin.LinearTv.Store;
using LinearTv.Builder.Assemble;
using LinearTv.Builder.Encode;
using LinearTv.Builder.Import;
using LinearTv.Builder.Library;

namespace LinearTv.Builder;

/// <summary>One item ready to encode: what it is, where this machine reads it from, and which tracks go in.</summary>
internal sealed record PlannedItem(SourceItem Item, string LocalPath, TrackChoice Choice, string? LocalSubtitlePath, Origin Origin);

/// <summary>
/// A resolved channel worked out down to the programmes in its file: which items, with which
/// audio and subtitles, in what order, at what frame rate, and which items had to be left out.
/// </summary>
internal sealed class ChannelPlan
{
    public required ResolvedChannel Channel { get; init; }

    /// <summary>Gets each usable item once, in listed order.</summary>
    public required IReadOnlyList<PlannedItem> Items { get; init; }

    /// <summary>Gets the programmes in file order: <see cref="Items"/> once per pass.</summary>
    public required IReadOnlyList<PlannedItem> Programmes { get; init; }

    public required FrameRate FrameRate { get; init; }

    public Resolution Resolution => Channel.Resolution;

    /// <summary>Gets the items left out, with why.</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>Gets things worth knowing about items that are in.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public TimeSpan Length => TimeSpan.FromTicks(Programmes.Sum(p => p.Item.RuntimeTicks));

    public bool Chunked => Length >= ChannelAssembler.ChunkThreshold;

    /// <summary>Works out a channel's plan from its sources.</summary>
    public static async Task<ChannelPlan> CreateAsync(
        ResolvedChannel channel, LibraryCatalogue catalogue, JellyfinClient client, OriginResolver origins, PathMap paths, CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        var items = new List<PlannedItem>();
        var skipped = new List<string>();
        var warnings = new List<string>();

        foreach (var source in channel.Sources)
        {
            foreach (var item in await client.ExpandAsync(catalogue.Find(source.Id)!, ct).ConfigureAwait(false))
            {
                if (!seen.Add(item.Id))
                {
                    continue;
                }

                if (Unusable(item, paths) is { } reason)
                {
                    skipped.Add($"{item}: {reason}");
                    continue;
                }

                var origin = await origins.ResolveAsync(item, ct).ConfigureAwait(false);
                var choice = TrackSelector.Choose(item, origin.Language, source.Options, channel.Language);
                if (choice.Skip is { } why)
                {
                    skipped.Add($"{item}: {why}");
                    continue;
                }

                string? subtitlePath = null;
                if (choice.Subtitle?.ExternalPath is { } external)
                {
                    subtitlePath = paths.ToLocal(external);
                    if (!File.Exists(subtitlePath) || !CanRead(subtitlePath))
                    {
                        skipped.Add($"{item}: can't read its subtitle file {subtitlePath}");
                        continue;
                    }
                }

                if (item.Video!.DolbyVisionOnly)
                {
                    warnings.Add($"{item}: Dolby Vision without an HDR10 base layer; its colours will be off");
                }

                if (origin.Language is null && item.AudioTracks.Select(t => t.Language).Distinct().Count() > 1)
                {
                    warnings.Add($"{item}: original language unknown ({origin.Source}); took the first audio track, {Languages.Name(choice.Audio?.Language)}");
                }

                items.Add(new PlannedItem(item, paths.ToLocal(item.Path!), choice, subtitlePath, origin));
            }
        }

        return new ChannelPlan
        {
            Channel = channel,
            Items = items,
            Programmes = Lineup.Build(items, channel.Shuffle, channel.Passes, channel.Number),
            FrameRate = FrameRate.ForChannel(items.Select(i => i.Item)),
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    private static string? Unusable(SourceItem item, PathMap paths)
    {
        if (item.Path is null)
        {
            return "Jellyfin has no file for it";
        }

        if (item.RuntimeTicks <= 0)
        {
            return "not probed by Jellyfin yet (no running time)";
        }

        if (item.Video is null)
        {
            return "no video stream";
        }

        var local = paths.ToLocal(item.Path);
        if (Directory.Exists(local))
        {
            return $"{local} is a folder (a disc rip?); only files can be encoded";
        }

        if (!File.Exists(local))
        {
            return $"can't open {local}{(local == item.Path ? " (no --map for this path?)" : string.Empty)}";
        }

        return CanRead(local) ? null : $"can't read {local}: permission denied (check the NAS share's permissions for this PC's login)";
    }

    /// <summary>
    /// Whether this machine may read the file, not just see it. On a NAS share a file can be listed
    /// but refuse reads; found here, it's a line in the check rather than a failed encode.
    /// </summary>
    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The guide entry for an item, without its place in the timeline.</summary>
    public static ManifestProgramme GuideEntry(SourceItem item, string? image) => new()
    {
        Title = item.IsEpisode ? item.SeriesName ?? item.Name : item.Name,
        EpisodeTitle = item.IsEpisode ? item.Name : null,
        Season = item.IsEpisode ? item.Season : null,
        Episode = item.IsEpisode ? item.Episode : null,
        Year = item.Year,
        Movie = item.Type == "Movie",
        Overview = item.Overview,
        Genres = item.Genres.Count > 0 ? [.. item.Genres] : null,
        Rating = item.Rating,
        Image = image,
    };
}
