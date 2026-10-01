using System.Text;
using Jellyfin.Plugin.LinearTv.Configuration;

namespace Jellyfin.Plugin.LinearTv.Import;

/// <summary>How imported channels combine with the existing ones.</summary>
internal enum ImportMode
{
    /// <summary>Imported channels replace existing channels with the same number; others are kept.</summary>
    Merge,

    /// <summary>The imported channels become the whole channel list.</summary>
    Replace,
}

/// <summary>What an import did, or would do. Returned for dry runs and real imports alike.</summary>
internal sealed class ImportReport
{
    public bool DryRun { get; set; }

    public bool Saved { get; set; }

    public string Mode { get; set; } = "merge";

    /// <summary>Gets a value indicating whether the import can be saved: nothing failed to resolve.</summary>
    public bool Ok => Errors.Count == 0 && Channels.TrueForAll(c => c.Problems.Count == 0);

    /// <summary>Gets problems with the document as a whole.</summary>
    public List<string> Errors { get; } = [];

    public List<ChannelReport> Channels { get; } = [];

    /// <summary>Gets the channels a replace-mode import removes.</summary>
    public List<string> Removed { get; } = [];
}

/// <summary>One channel's part of an <see cref="ImportReport"/>.</summary>
internal sealed class ChannelReport
{
    public string? Number { get; set; }

    public string? Name { get; set; }

    public bool Shuffle { get; set; }

    /// <summary>Gets or sets one of create, replace, unchanged.</summary>
    public string? Action { get; set; }

    public List<ResolvedContent> Content { get; } = [];

    /// <summary>Gets what stops the import being saved.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Gets things worth knowing that don't stop the import.</summary>
    public List<string> Warnings { get; } = [];
}

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
/// Resolves a <see cref="ChannelDocument"/> against the library and merges it into the channel list.
/// Pure: no I/O and no Jellyfin services, so it is tested directly.
/// </summary>
/// <remarks>
/// Matching is forgiving where sources commonly disagree, and strict where guessing would be
/// worse than asking. Titles ignore case, punctuation and accents. A year may be off by one
/// (release years differ between databases). Anything missing or ambiguous is reported with
/// suggestions, never guessed.
/// </remarks>
internal static class ChannelImporter
{
    public static (ImportReport Report, List<ChannelDefinition> Channels) Resolve(
        ChannelDocument document, LibraryCatalogue catalogue)
    {
        var report = new ImportReport();
        var channels = new List<ChannelDefinition>();

        if (document.Channels.Count == 0)
        {
            report.Errors.Add("The document has no channels.");
        }

        foreach (var duplicate in document.Channels
            .Where(c => !string.IsNullOrWhiteSpace(c.Number))
            .GroupBy(c => c.Number!.Trim())
            .Where(g => g.Count() > 1))
        {
            report.Errors.Add($"Channel number {duplicate.Key} appears more than once.");
        }

        foreach (var spec in document.Channels)
        {
            var channel = new ChannelReport { Number = spec.Number?.Trim(), Name = spec.Name?.Trim(), Shuffle = spec.Shuffle };
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

            var ids = new List<Guid>();
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
                    }
                }

                channel.Content.Add(resolved);
            }

            report.Channels.Add(channel);
            channels.Add(new ChannelDefinition
            {
                Number = channel.Number ?? string.Empty,
                Name = channel.Name ?? string.Empty,
                Shuffle = spec.Shuffle,
                SourceIds = ids,
            });
        }

        return (report, channels);
    }

    /// <summary>
    /// The channel list after importing. Never modifies <paramref name="existing"/>, so a dry run
    /// can call this safely. Records each channel's action in <paramref name="report"/>.
    /// </summary>
    public static List<ChannelDefinition> Merge(
        IReadOnlyList<ChannelDefinition> existing,
        IReadOnlyList<ChannelDefinition> imported,
        ImportMode mode,
        ImportReport report)
    {
        report.Mode = mode == ImportMode.Merge ? "merge" : "replace";
        var byNumber = existing.GroupBy(c => c.Number.Trim()).ToDictionary(g => g.Key, g => g.First());
        var result = mode == ImportMode.Merge ? existing.Select(Copy).ToList() : [];

        for (var i = 0; i < imported.Count; i++)
        {
            var incoming = Copy(imported[i]);
            if (byNumber.TryGetValue(incoming.Number, out var current))
            {
                // Same number: keep the channel's identity, so the guide treats it as the same channel.
                incoming.Id = current.Id;
                report.Channels[i].Action = SameContent(current, incoming) ? "unchanged" : "replace";
                if (mode == ImportMode.Merge)
                {
                    result[result.FindIndex(c => c.Id == current.Id)] = incoming;
                    continue;
                }
            }
            else
            {
                report.Channels[i].Action = "create";
            }

            result.Add(incoming);
        }

        if (mode == ImportMode.Replace)
        {
            var kept = imported.Select(c => c.Number).ToHashSet();
            report.Removed.AddRange(existing.Where(c => !kept.Contains(c.Number.Trim())).Select(c => $"{c.Number} {c.Name}"));
        }

        return result;
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

    private static bool SameContent(ChannelDefinition a, ChannelDefinition b)
        => a.Name == b.Name && a.Shuffle == b.Shuffle && a.SourceIds.SequenceEqual(b.SourceIds);

    private static ChannelDefinition Copy(ChannelDefinition c) => new()
    {
        Id = c.Id,
        Number = c.Number,
        Name = c.Name,
        Shuffle = c.Shuffle,
        SourceIds = [.. c.SourceIds],
    };

    private static string? Join(string? a, string? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => $"{a}; {b}",
    };
}
