namespace LinearTv.Builder.Import;

/// <summary>Kinds of content a channel can reference by name.</summary>
internal static class ContentKind
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Collection = "collection";
    public const string Library = "library";
    public const string Episode = "episode";

    /// <summary>The canonical kind for a user-supplied type, or null if it isn't one.</summary>
    public static string? Parse(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" => Movie,
        "series" or "show" or "tv" => Series,
        "collection" or "boxset" => Collection,
        "library" => Library,
        "episode" => Episode,
        _ => null,
    };
}

/// <summary>One referenceable item of the library.</summary>
internal sealed record CatalogueItem(
    Guid Id,
    string Kind,
    string Name,
    string? OriginalTitle = null,
    int? Year = null,
    Guid? SeriesId = null,
    string? SeriesName = null,
    int? Season = null,
    int? Episode = null)
{
    public override string ToString() => Kind == ContentKind.Episode
        ? $"{SeriesName} S{Season:00}E{Episode:00}"
        : $"{Name}{(Year is { } y ? $" ({y})" : string.Empty)}";
}

/// <summary>A snapshot of the library's referenceable items, for resolving names.</summary>
internal sealed class LibraryCatalogue
{
    private readonly Dictionary<Guid, CatalogueItem> _byId;

    public LibraryCatalogue(IEnumerable<CatalogueItem> items)
    {
        Items = items.ToList();
        _byId = Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<CatalogueItem> Items { get; }

    public CatalogueItem? Find(Guid id) => _byId.GetValueOrDefault(id);

    public IEnumerable<CatalogueItem> OfKind(string kind) => Items.Where(i => i.Kind == kind);
}
