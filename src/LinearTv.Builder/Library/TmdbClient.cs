using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LinearTv.Builder.Library;

/// <summary>
/// Looks up a film's or series' original language on TMDb, by the TMDb id Jellyfin matched.
/// Answers are kept in a small JSON file, so each title is asked about once.
/// </summary>
/// <remarks>
/// Takes either kind of TMDb credential: a v3 API key (sent as <c>api_key</c>) or a v4 read
/// access token (a JWT, sent as a bearer token).
/// </remarks>
internal sealed class TmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _cacheFile;
    private readonly Dictionary<string, string> _cache;

    public TmdbClient(string credential, string cacheFile, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (credential.StartsWith("eyJ", StringComparison.Ordinal))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        else
        {
            _apiKey = credential;
        }

        _cacheFile = cacheFile;
        _cache = File.Exists(cacheFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cacheFile)) ?? []
            : [];
    }

    /// <summary>The original language, canonical; null if TMDb has no such title or no language for it.</summary>
    /// <param name="kind"><c>movie</c> or <c>tv</c>.</param>
    public async Task<string?> OriginalLanguageAsync(string kind, string tmdbId, CancellationToken ct)
    {
        var key = $"{kind}/{tmdbId}";
        if (!_cache.TryGetValue(key, out var language))
        {
            using var response = await _http.GetAsync(_apiKey is null ? key : $"{key}?api_key={Uri.EscapeDataString(_apiKey)}", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException("TMDb rejected the API key");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                language = string.Empty;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
                language = doc.RootElement.TryGetProperty("original_language", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            }

            _cache[key] = language;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
            await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(_cache), ct).ConfigureAwait(false);
        }

        return Languages.Normalize(language);
    }

    public void Dispose() => _http.Dispose();
}
