using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LinearTv.Import;

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

/// <summary>Builds a <see cref="LibraryCatalogue"/> from the Jellyfin library.</summary>
internal sealed class LibraryCatalogueSource(ILibraryManager libraryManager)
{
    public LibraryCatalogue Build()
    {
        var items = new List<CatalogueItem>();

        foreach (var item in Query(BaseItemKind.Movie))
        {
            items.Add(new(item.Id, ContentKind.Movie, item.Name, item.OriginalTitle, item.ProductionYear));
        }

        foreach (var item in Query(BaseItemKind.Series))
        {
            items.Add(new(item.Id, ContentKind.Series, item.Name, item.OriginalTitle, item.ProductionYear));
        }

        foreach (var item in Query(BaseItemKind.BoxSet))
        {
            items.Add(new(item.Id, ContentKind.Collection, item.Name));
        }

        foreach (var episode in Query(BaseItemKind.Episode).OfType<Episode>())
        {
            items.Add(new(
                episode.Id, ContentKind.Episode, episode.Name,
                SeriesId: episode.SeriesId, SeriesName: episode.SeriesName,
                Season: episode.ParentIndexNumber, Episode: episode.IndexNumber));
        }

        foreach (var folder in libraryManager.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                items.Add(new(id, ContentKind.Library, folder.Name));
            }
        }

        return new LibraryCatalogue(items);
    }

    private IReadOnlyList<BaseItem> Query(BaseItemKind kind)
        => libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            IsVirtualItem = false,
        });
}
