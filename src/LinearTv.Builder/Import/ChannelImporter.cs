using System.Text;
using LinearTv.Builder.Encode;
using LinearTv.Builder.Library;

namespace LinearTv.Builder.Import;

/// <summary>How a channel file resolved against the library.</summary>
internal sealed class ImportReport
{
    /// <summary>Gets a value indicating whether everything resolved, so the channels can be built.</summary>
    public bool Ok => Errors.Count == 0 && Channels.TrueForAll(c => c.Problems.Count == 0);

    /// <summary>Gets problems with the document as a whole.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Gets or sets the viewer's language, canonical.</summary>
    public string Language { get; set; } = Languages.English;

    public List<ChannelReport> Channels { get; } = [];
}

/// <summary>One channel's part of an <see cref="ImportReport"/>.</summary>
internal sealed class ChannelReport
{
    public string? Number { get; set; }

    public string? Name { get; set; }

    public bool Shuffle { get; set; }

    public int Passes { get; set; } = 1;

    public string Resolution { get; set; } = "720p";

    public List<ResolvedContent> Content { get; } = [];

    /// <summary>Gets what stops the channel being built.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Gets things worth knowing that don't stop the build.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>A channel whose content all resolved: its sources, in the order listed, each with its track options.</summary>
internal sealed record ResolvedChannel(
    string Number, string Name, bool Shuffle, int Passes, IReadOnlyList<ResolvedSource> Sources, string Language)
{
    /// <summary>Gets the picture size every programme is fitted to.</summary>
    public Resolution Resolution { get; init; } = Resolution.Default;
}

/// <summary>One resolved content entry: the library item, and the audio and subtitle rules for what it expands to.</summary>
internal sealed record ResolvedSource(Guid Id, TrackOptions Options);

/// <summary>How one content reference resolved.</summary>
internal sealed class ResolvedContent
{
    public string Requested { get; set; } = string.Empty;

    public string? Matched { get; set; }

    public string? Type { get; set; }

    public string? Id { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Resolves a <see cref="ChannelDocument"/> against the library. Pure: no I/O, so it is tested
/// directly.
/// </summary>
/// <remarks>
/// Matching is forgiving where sources commonly disagree, and strict where guessing would be
/// worse than asking. Titles ignore case, punctuation and accents. A year may be off by one
/// (release years differ between databases). Anything missing or ambiguous is reported with
/// suggestions, never guessed.
/// </remarks>
internal static class ChannelImporter
{
    /// <summary>Most passes a channel file may hold: each one is the whole lineup again.</summary>
    public const int MaxPasses = 20;

    public static (ImportReport Report, List<ResolvedChannel> Channels) Resolve(
        ChannelDocument document, LibraryCatalogue catalogue)
    {
        var report = new ImportReport();
        var channels = new List<ResolvedChannel>();

        if (document.Channels.Count == 0)
        {
            report.Errors.Add("The document has no channels.");
        }

        var viewer = Languages.English;
        if (document.Language is { } language)
        {
            if (Languages.Normalize(language) is { } known)
            {
                viewer = known;
            }
            else
            {
                report.Errors.Add($"language '{language}' isn't a language code or name this builder knows.");
            }
        }

        report.Language = viewer;

        foreach (var duplicate in document.Channels
            .Where(c => !string.IsNullOrWhiteSpace(c.Number))
            .GroupBy(c => c.Number!.Trim())
            .Where(g => g.Count() > 1))
        {
            report.Errors.Add($"Channel number {duplicate.Key} appears more than once.");
        }

        foreach (var spec in document.Channels)
        {
            var channel = new ChannelReport
            {
                Number = spec.Number?.Trim(), Name = spec.Name?.Trim(), Shuffle = spec.Shuffle, Passes = spec.Passes ?? 1,
            };
            if (string.IsNullOrWhiteSpace(channel.Number))
            {
                channel.Problems.Add("Missing channel number.");
            }

            if (string.IsNullOrWhiteSpace(channel.Name))
            {
                channel.Problems.Add("Missing channel name.");
            }

            if (spec.Content.Count == 0)
            {
                channel.Problems.Add("No content listed.");
            }

            if (spec.Passes is < 1 or > MaxPasses)
            {
                channel.Problems.Add($"passes must be from 1 to {MaxPasses}, not {spec.Passes}.");
            }

            var resolution = spec.Resolution is null ? Resolution.Default : Resolution.Parse(spec.Resolution);
            if (resolution is null)
            {
                channel.Problems.Add($"resolution must be 720p or 1080p, not '{spec.Resolution}'.");
            }
            else if (spec.Cq is { } cq)
            {
                if (cq is < 15 or > 40)
                {
                    channel.Problems.Add($"cq must be from 15 to 40, not {cq}.");
                }

                resolution = resolution with { Cq = cq };
            }

            if (resolution is not null)
            {
                channel.Resolution = resolution.ToString();
            }

            var channelOptions = ParseOptions(spec.Audio, spec.Subtitles, TrackOptions.Default, channel.Problems, "channel");
            var ids = new List<Guid>();
            var sources = new List<ResolvedSource>();
            foreach (var reference in spec.Content)
            {
                var resolved = new ResolvedContent { Requested = reference.ToString() };
                var (item, problem, note) = ResolveReference(reference, catalogue);
                if (item is null)
                {
                    resolved.Note = problem;
                    channel.Problems.Add($"{resolved.Requested}: {problem}");
                }
                else
                {
                    resolved.Matched = item.ToString();
                    resolved.Type = item.Kind;
                    resolved.Id = item.Id.ToString("N");
                    resolved.Note = note;
                    if (ids.Contains(item.Id))
                    {
                        channel.Warnings.Add($"{resolved.Requested} is listed more than once; it plays once per loop.");
                    }
                    else
                    {
                        ids.Add(item.Id);
                        sources.Add(new ResolvedSource(
                            item.Id, ParseOptions(reference.Audio, reference.Subtitles, channelOptions, channel.Problems, resolved.Requested)));
                    }
                }

                channel.Content.Add(resolved);
            }

            report.Channels.Add(channel);
            channels.Add(new ResolvedChannel(channel.Number ?? string.Empty, channel.Name ?? string.Empty, spec.Shuffle, channel.Passes, sources, viewer)
            {
                Resolution = resolution ?? Resolution.Default,
            });
        }

        return (report, channels);
    }

    /// <summary>An entry's (or channel's) audio and subtitle options, each inheriting from <paramref name="inherited"/> when unset.</summary>
    private static TrackOptions ParseOptions(string? audio, string? subtitles, TrackOptions inherited, List<string> problems, string what)
    {
        var options = inherited;
        if (audio is not null)
        {
            if (Languages.Normalize(audio) is { } language)
            {
                options = options with { Audio = language };
            }
            else
            {
                problems.Add($"{what}: audio '{audio}' isn't a language code or name this builder knows.");
            }
        }

        if (subtitles is not null)
        {
            if (Enum.TryParse<SubtitleMode>(subtitles.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                options = options with { Subtitles = mode };
            }
            else
            {
                problems.Add($"{what}: subtitles must be auto, none, forced or full, not '{subtitles}'.");
            }
        }

        return options;
    }

    internal static (CatalogueItem? Item, string? Problem, string? Note) ResolveReference(
        ContentRef reference, LibraryCatalogue catalogue)
    {
        var kind = ContentKind.Parse(reference.Type);
        string? idNote = null;

        if (!string.IsNullOrWhiteSpace(reference.Id))
        {
            if (Guid.TryParse(reference.Id, out var id) && catalogue.Find(id) is { } byId)
            {
                return kind is null || kind == byId.Kind
                    ? (byId, null, null)
                    : (null, $"id {reference.Id} is a {byId.Kind} ({byId}), not a {kind}", null);
            }

            if (string.IsNullOrWhiteSpace(reference.Title) && string.IsNullOrWhiteSpace(reference.Series))
            {
                return (null, $"no item with id {reference.Id} on this server, and no title to find it by", null);
            }

            // Most likely an id from another server: ids derive from file paths.
            idNote = "id not on this server; matched by name";
        }

        if (kind is null)
        {
            return (null, reference.Type is null
                ? "missing type (movie, series, collection, library or episode)"
                : $"unknown type '{reference.Type}' (use movie, series, collection, library or episode)", null);
        }

        var (item, problem, note) = kind == ContentKind.Episode
            ? ResolveEpisode(reference, catalogue)
            : ResolveTitled(reference.Title, reference.Year, kind, catalogue);
        return (item, problem, Join(idNote, note));
    }

    /// <summary>Comparison key for titles: letters and digits only, lower case, accents removed.</summary>
    internal static string Key(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormKD))
        {
            // Decomposition separates accents into combining marks, which aren't letters, so
            // "Amélie" and "Amelie" share a key. The compatibility form also turns superscripts
            // into digits: TMDb titles "Alien³" and "The Accountant²" must not lose their number.
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static (CatalogueItem? Item, string? Problem, string? Note) ResolveTitled(
        string? title, int? year, string kind, LibraryCatalogue catalogue)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, "missing title", null);
        }

        var key = Key(title);
        var matches = catalogue.OfKind(kind)
            .Where(i => Key(i.Name) == key || (i.OriginalTitle is { } original && Key(original) == key))
            .ToList();

        if (matches.Count == 0)
        {
            // The commonest slip, by hand or by AI: the right title under the wrong type.
            var otherKind = catalogue.Items
                .Where(i => i.Kind != kind && i.Kind != ContentKind.Episode && Key(i.Name) == key)
                .Select(i => $"{i} is a {i.Kind}")
                .ToList();
            if (otherKind.Count > 0)
            {
                return (null, $"no {kind} of that name, but {string.Join(", and ", otherKind)}", null);
            }

            // Titles often differ by a subtitle or possessive: "Die Hard 2: Die Harder" is TMDb's
            // "Die Hard 2", "Dracula" (1992) is "Bram Stoker's Dracula". One title containing the
            // other is safe to accept only with the year to confirm it, and only if unique. Only
            // the closest overlaps count, or "Die Hard 2: Die Harder" with a wrong year of 1988
            // would match "Die Hard" (1988) rather than being refused.
            if (year is { } y)
            {
                var overlapping = catalogue.OfKind(kind)
                    .Select(i => (Item: i, Score: OverlapLength(i, key)))
                    .Where(o => o.Score > 0)
                    .ToList();
                var best = overlapping.Count == 0 ? 0 : overlapping.Max(o => o.Score);
                var partial = overlapping.Where(o => o.Score == best && o.Item.Year == y).ToList();
                if (partial.Count == 1)
                {
                    return (partial[0].Item, null, $"partial title match in {y}");
                }
            }

            return (null, $"not found{Suggestions(key, kind, catalogue)}", null);
        }

        string? note = null;
        if (year is { } wanted && kind is ContentKind.Movie or ContentKind.Series)
        {
            var exact = matches.Where(m => m.Year == wanted).ToList();
            var near = matches.Where(m => m.Year is { } y && Math.Abs(y - wanted) == 1).ToList();
            var undated = matches.Where(m => m.Year is null).ToList();
            if (exact.Count > 0)
            {
                matches = exact;
            }
            else if (near.Count > 0)
            {
                matches = near;
                note = $"library has {near[0].Year}, not {wanted}";
            }
            else if (undated.Count > 0)
            {
                matches = undated;
                note = $"library has no year for it; asked for {wanted}";
            }
            else
            {
                return (null, $"found {string.Join(", ", matches)}, but not from {wanted}", null);
            }
        }

        return matches.Count == 1
            ? (matches[0], null, note)
            : (null, $"ambiguous: {string.Join(", ", matches)}; add a year to choose", null);
    }

    private static (CatalogueItem? Item, string? Problem, string? Note) ResolveEpisode(
        ContentRef reference, LibraryCatalogue catalogue)
    {
        if (string.IsNullOrWhiteSpace(reference.Series) || reference.Season is null || reference.Episode is null)
        {
            return (null, "an episode needs series, season and episode", null);
        }

        // The year, if given, belongs to the series.
        var (series, problem, note) = ResolveTitled(reference.Series, reference.Year, ContentKind.Series, catalogue);
        if (series is null)
        {
            return (null, $"series {problem}", null);
        }

        var episode = catalogue.OfKind(ContentKind.Episode).FirstOrDefault(e =>
            e.SeriesId == series.Id && e.Season == reference.Season && e.Episode == reference.Episode);
        return episode is null
            ? (null, $"{series.Name} has no S{reference.Season:00}E{reference.Episode:00} in the library", null)
            : (episode, null, note);
    }

    private static string Suggestions(string key, string kind, LibraryCatalogue catalogue)
    {
        // Containment either way catches "Alien" vs "Alien Earth", or a missing subtitle. Short
        // keys would match far too much, so they get no suggestions.
        if (key.Length < 4)
        {
            return string.Empty;
        }

        var close = catalogue.OfKind(kind)
            .Select(i => (Item: i, Score: OverlapLength(i, key)))
            .Where(o => o.Score > 0)
            .OrderByDescending(o => o.Score)
            .Take(3)
            .Select(o => o.Item)
            .ToList();
        return close.Count == 0 ? string.Empty : $"; did you mean {string.Join(", ", close)}?";
    }

    // When one title's key contains the other's, the length of the shorter (how much of the
    // title is shared); otherwise 0. Short keys would overlap with far too much, so they score 0.
    private static int OverlapLength(CatalogueItem item, string key)
        => key.Length < 4 ? 0 : new[] { item.Name, item.OriginalTitle }
            .Select(t => t is null ? string.Empty : Key(t))
            .Where(k => k.Length >= 4 && (k.Contains(key, StringComparison.Ordinal) || key.Contains(k, StringComparison.Ordinal)))
            .Select(k => Math.Min(k.Length, key.Length))
            .DefaultIfEmpty(0)
            .Max();

    private static string? Join(string? a, string? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => $"{a}; {b}",
    };
}
