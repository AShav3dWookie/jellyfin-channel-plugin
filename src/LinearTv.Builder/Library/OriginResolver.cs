namespace LinearTv.Builder.Library;

/// <summary>Where an item's original language came from, for the report.</summary>
internal sealed record Origin(string? Language, string Source);

/// <summary>
/// An item's original language: TMDb's, by the film's (or for an episode, the series') TMDb id;
/// failing that, the first production country with a clear language. Unknown otherwise.
/// </summary>
internal sealed class OriginResolver(JellyfinClient jellyfin, TmdbClient? tmdb)
{
    private readonly Dictionary<Guid, (string? TmdbId, IReadOnlyList<string> Countries)> _series = [];

    public async Task<Origin> ResolveAsync(SourceItem item, CancellationToken ct)
    {
        var (kind, tmdbId, countries) = (item.IsEpisode ? "tv" : "movie", item.TmdbId, item.ProductionCountries);
        if (item.IsEpisode && item.SeriesId is { } seriesId)
        {
            if (!_series.TryGetValue(seriesId, out var series))
            {
                series = await jellyfin.GetOriginInfoAsync(seriesId, ct).ConfigureAwait(false);
                _series[seriesId] = series;
            }

            (tmdbId, countries) = (series.TmdbId, series.Countries);
        }

        if (tmdb is not null && tmdbId is not null && await tmdb.OriginalLanguageAsync(kind, tmdbId, ct).ConfigureAwait(false) is { } language)
        {
            return new Origin(language, "TMDb");
        }

        return Languages.FromCountries(countries) is { } fromCountry
            ? new Origin(fromCountry, $"production country {countries.First(c => Languages.FromCountries([c]) is not null)}")
            : new Origin(null, tmdb is null ? "no TMDb key, no clear production country" : "not on TMDb, no clear production country");
    }
}
