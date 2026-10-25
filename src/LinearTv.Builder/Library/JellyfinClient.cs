using System.Net.Http.Headers;
using System.Text.Json;
using LinearTv.Builder.Import;

namespace LinearTv.Builder.Library;

/// <summary>
/// The parts of Jellyfin's REST API the builder reads: the library's referenceable items, the
/// playable items behind each one, and artwork. Read-only; it never changes the server.
/// </summary>
internal sealed class JellyfinClient : IDisposable
{
    private static readonly string[] PlayableTypes = ["Movie", "Episode", "Video", "MusicVideo"];

    private readonly HttpClient _http;

    public JellyfinClient(string serverUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{apiKey}\"");
    }

    public string Server => _http.BaseAddress!.ToString().TrimEnd('/');

    /// <summary>Everything a channel file can name: films, series, collections, episodes, libraries.</summary>
    public async Task<LibraryCatalogue> GetCatalogueAsync(CancellationToken ct)
    {
        var items = new List<CatalogueItem>();

        // CollapseBoxSetItems=false: with "group movies into collections" on, Jellyfin otherwise
        // hides every film that belongs to a collection inside it.
        using (var doc = await GetJsonAsync(
            "Items?Recursive=true&IncludeItemTypes=Movie,Series,BoxSet,Episode&IsMissing=false&CollapseBoxSetItems=false"
            + "&Fields=OriginalTitle,ProductionYear&EnableImages=false&EnableUserData=false", ct).ConfigureAwait(false))
        {
            foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
            {
                var id = Guid.Parse(item.GetProperty("Id").GetString()!);
                var name = Str(item, "Name") ?? string.Empty;
                switch (Str(item, "Type"))
                {
                    case "Movie":
                        items.Add(new(id, ContentKind.Movie, name, Str(item, "OriginalTitle"), Int(item, "ProductionYear")));
                        break;
                    case "Series":
                        items.Add(new(id, ContentKind.Series, name, Str(item, "OriginalTitle"), Int(item, "ProductionYear")));
                        break;
                    case "BoxSet":
                        items.Add(new(id, ContentKind.Collection, name));
                        break;
                    case "Episode":
                        items.Add(new(
                            id, ContentKind.Episode, name,
                            SeriesId: Str(item, "SeriesId") is { } s ? Guid.Parse(s) : null,
                            SeriesName: Str(item, "SeriesName"),
                            Season: Int(item, "ParentIndexNumber"),
                            Episode: Int(item, "IndexNumber")));
                        break;
                }
            }
        }

        using (var doc = await GetJsonAsync("Library/VirtualFolders", ct).ConfigureAwait(false))
        {
            foreach (var folder in doc.RootElement.EnumerateArray())
            {
                if (Guid.TryParse(Str(folder, "ItemId"), out var id))
                {
                    items.Add(new(id, ContentKind.Library, Str(folder, "Name") ?? string.Empty));
                }
            }
        }

        return new LibraryCatalogue(items);
    }

    /// <summary>
    /// The playable items behind a channel source, in play order: a series' episodes in order,
    /// a collection's films, a library's contents, or the item itself.
    /// </summary>
    public async Task<IReadOnlyList<SourceItem>> ExpandAsync(CatalogueItem source, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case ContentKind.Movie or ContentKind.Episode:
                return await QueryAsync($"Ids={source.Id:N}", ct).ConfigureAwait(false);

            case ContentKind.Series:
                return Ordered(await QueryAsync($"ParentId={source.Id:N}&Recursive=true&IncludeItemTypes=Episode", ct).ConfigureAwait(false));

            case ContentKind.Library:
                return Ordered(await QueryAsync(
                    $"ParentId={source.Id:N}&Recursive=true&IncludeItemTypes={string.Join(',', PlayableTypes)}", ct).ConfigureAwait(false));

            case ContentKind.Collection:
                // A collection lists its members in its own order; a series in it plays in full.
                var result = new List<SourceItem>();
                using (var doc = await GetJsonAsync($"Items?ParentId={source.Id:N}&Fields=Path&EnableImages=false&EnableUserData=false", ct).ConfigureAwait(false))
                {
                    foreach (var child in doc.RootElement.GetProperty("Items").EnumerateArray())
                    {
                        var id = Guid.Parse(child.GetProperty("Id").GetString()!);
                        var type = Str(child, "Type");
                        if (type == "Series")
                        {
                            result.AddRange(Ordered(await QueryAsync($"ParentId={id:N}&Recursive=true&IncludeItemTypes=Episode", ct).ConfigureAwait(false)));
                        }
                        else if (PlayableTypes.Contains(type))
                        {
                            result.AddRange(await QueryAsync($"Ids={id:N}", ct).ConfigureAwait(false));
                        }
                    }
                }

                return result;

            default:
                return [];
        }
    }

    /// <summary>A film's or series' TMDb id and production countries, for working out its original language.</summary>
    public async Task<(string? TmdbId, IReadOnlyList<string> Countries)> GetOriginInfoAsync(Guid id, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"Items?Ids={id:N}&Fields=ProviderIds,ProductionLocations&EnableImages=false&EnableUserData=false", ct).ConfigureAwait(false);
        var items = doc.RootElement.GetProperty("Items");
        if (items.GetArrayLength() == 0)
        {
            return (null, []);
        }

        return (SourceItem.TmdbIdOf(items[0]), SourceItem.Strings(items[0], "ProductionLocations"));
    }

    /// <summary>An item's primary image as a JPEG, or null if it has none.</summary>
    public async Task<byte[]?> GetImageAsync(string itemId, string tag, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"Items/{itemId}/Images/Primary?tag={Uri.EscapeDataString(tag)}&maxWidth=600&format=Jpg&quality=90", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Series order: by series (or title), then season with specials and unnumbered seasons last,
    /// then episode. The same order 0.x channels played in.
    /// </summary>
    internal static List<SourceItem> Ordered(IEnumerable<SourceItem> items)
        => items
            .OrderBy(i => i.SeriesName ?? i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Season is null or 0 ? int.MaxValue : i.Season.Value)
            .ThenBy(i => i.Episode ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<List<SourceItem>> QueryAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"Items?{query}&IsMissing=false&Fields={SourceItem.Fields}&EnableUserData=false", ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("Items").EnumerateArray()
            .Where(i => PlayableTypes.Contains(Str(i, "Type")) && !Bool(i, "IsFolder"))
            .Select(SourceItem.FromJson)
            .ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => $"{Server} rejected the API key",
                System.Net.HttpStatusCode.Forbidden => $"the API key on {Server} isn't allowed to read the library",
                _ => $"{Server} answered HTTP {(int)response.StatusCode} for /{path.Split('?')[0]}",
            });
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
