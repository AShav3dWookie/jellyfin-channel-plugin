using System.Net;
using System.Text.Json;
using LinearTv.Builder.Library;

namespace LinearTv.Builder.Tests;

public class LibraryTests
{
    // ---- languages ----

    [Theory]
    [InlineData("fr", "fra")]
    [InlineData("fre", "fra")]
    [InlineData("FRA", "fra")]
    [InlineData("French", "fra")]
    [InlineData("ja", "jpn")]
    [InlineData("nob", "nor")]
    [InlineData("cn", "zho")]   // TMDb's Cantonese
    [InlineData("chi", "zho")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData("klingon", null)]
    public void Languages_NormalizeEveryCommonSpelling(string code, string? expected)
        => Assert.Equal(expected, Languages.Normalize(code));

    [Fact]
    public void Languages_FromCountries_SkipsCountriesWithoutOneClearLanguage()
    {
        Assert.Equal("ron", Languages.FromCountries(["Belgium", "Romania"]));
        Assert.Equal("eng", Languages.FromCountries(["United States of America", "Japan"]));
        Assert.Null(Languages.FromCountries(["Switzerland"]));
    }

    // ---- path mapping ----

    [Fact]
    public void PathMap_LongestPrefixWins_AndSeparatorsAreLocal()
    {
        var map = new PathMap(["/mnt/tank=X:", "/mnt/tank/media=\\\\TRUENAS\\media"]);

        Assert.Equal(
            Path.Combine("\\\\TRUENAS\\media", "Films", "A (2001)", "a.mkv").Replace('/', Path.DirectorySeparatorChar),
            map.ToLocal("/mnt/tank/media/Films/A (2001)/a.mkv"));
    }

    [Fact]
    public void PathMap_MatchesWholeFolderNamesOnly()
    {
        var map = new PathMap(["/media=D:"]);

        Assert.Equal("/mediaserver/x.mkv", map.ToLocal("/mediaserver/x.mkv"));
    }

    [Fact]
    public void PathMap_RejectsARuleWithoutAnEquals()
        => Assert.Throws<ArgumentException>(() => new PathMap(["/media"]));

    // ---- reading Jellyfin's items ----

    private const string ItemJson = """
        {
          "Id": "5f1a7c7e8b9d4e2fa3b4c5d6e7f80912", "Type": "Movie", "Name": "Spirited Away", "ProductionYear": 2001,
          "RunTimeTicks": 74880000000, "ProviderIds": { "Tmdb": "129", "Imdb": "tt0245429" },
          "ProductionLocations": ["Japan"], "ImageTags": { "Primary": "abc" },
          "MediaSources": [ {
            "Path": "/media/films/Spirited Away (2001)/Spirited Away.mkv", "DefaultAudioStreamIndex": 1,
            "MediaStreams": [
              { "Type": "Video", "Index": 0, "Codec": "hevc", "Width": 3840, "Height": 2160, "RealFrameRate": 23.976,
                "VideoRange": "HDR", "VideoRangeType": "HDR10", "IsInterlaced": false },
              { "Type": "Audio", "Index": 1, "Codec": "truehd", "Language": "eng", "Channels": 8, "IsDefault": true },
              { "Type": "Audio", "Index": 2, "Codec": "ac3", "Language": "jpn", "Channels": 6, "Title": "Japanese" },
              { "Type": "Subtitle", "Index": 3, "Codec": "PGSSUB", "Language": "eng", "Title": "Signs & Songs", "IsForced": true },
              { "Type": "Subtitle", "Index": 4, "Codec": "subrip", "Language": "eng", "IsTextSubtitleStream": true },
              { "Type": "Subtitle", "Index": 5, "Codec": "subrip", "Language": "eng", "IsExternal": true,
                "IsTextSubtitleStream": true, "Path": "/media/films/Spirited Away (2001)/Spirited Away.en.sdh.srt", "IsHearingImpaired": true }
            ] } ]
        }
        """;

    [Fact]
    public void SourceItem_ReadsEverythingTheRulesNeed()
    {
        using var doc = JsonDocument.Parse(ItemJson);
        var item = SourceItem.FromJson(doc.RootElement);

        Assert.Equal("Spirited Away (2001)", item.ToString());
        Assert.Equal("129", item.TmdbId);
        Assert.Equal(["Japan"], item.ProductionCountries);
        Assert.Equal(new VideoInfo(0, "hevc", 3840, 2160, 23.976, false, true, false), item.Video);
        Assert.Equal(["eng", "jpn"], item.AudioTracks.Select(t => t.Language));
        Assert.Equal(3, item.SubtitleTracks.Count);
        Assert.True(item.SubtitleTracks[0].IsForced);
        Assert.False(item.SubtitleTracks[0].IsText);
        Assert.Null(item.SubtitleTracks[1].ExternalPath);
        Assert.Equal("/media/films/Spirited Away (2001)/Spirited Away.en.sdh.srt", item.SubtitleTracks[2].ExternalPath);
        Assert.True(item.SubtitleTracks[2].IsHearingImpaired);
        Assert.Equal(("5f1a7c7e8b9d4e2fa3b4c5d6e7f80912", "abc"), (item.ImageItemId, item.ImageTag));
    }

    [Fact]
    public void SourceItem_ExternalSubtitlesNumberedFirst_DontShiftTheFilesOwnStreams()
    {
        // As Jellyfin 12.1 reports a film with an external .srt: the .srt is Index 0.
        using var doc = JsonDocument.Parse("""
            { "Id": "00000000000000000000000000000001", "Type": "Movie", "Name": "F", "MediaSources": [ { "MediaStreams": [
              { "Type": "Subtitle", "Index": 0, "Codec": "subrip", "IsExternal": true, "Path": "/m/F.en.srt", "IsTextSubtitleStream": true },
              { "Type": "Video", "Index": 1, "Codec": "h264" },
              { "Type": "Audio", "Index": 2, "Codec": "aac", "Language": "ger" },
              { "Type": "Subtitle", "Index": 3, "Codec": "subrip", "Language": "eng", "IsTextSubtitleStream": true } ] } ] }
            """);
        var item = SourceItem.FromJson(doc.RootElement);

        Assert.Equal(0, item.Video!.Index);
        Assert.Equal(1, item.AudioTracks.Single().Index);
        Assert.Equal(2, item.SubtitleTracks.Single(t => t.ExternalPath is null).Index);
    }

    [Fact]
    public void SourceItem_AnEpisodeWithoutAnImage_UsesTheSeriesPoster()
    {
        using var doc = JsonDocument.Parse("""
            { "Id": "00000000000000000000000000000001", "Type": "Episode", "Name": "Pilot", "SeriesName": "Show",
              "SeriesId": "00000000000000000000000000000002", "SeriesPrimaryImageTag": "s", "ParentIndexNumber": 1, "IndexNumber": 1 }
            """);
        var item = SourceItem.FromJson(doc.RootElement);

        Assert.Equal("Show S01E01", item.ToString());
        Assert.Equal(("00000000000000000000000000000002", "s"), (item.ImageItemId, item.ImageTag));
    }

    [Fact]
    public void JellyfinClient_OrdersEpisodes_SpecialsAfterTheRegularSeasons()
    {
        static SourceItem Ep(int? season, int? episode) => new()
        {
            Id = Guid.NewGuid(), Type = "Episode", Name = $"{season}x{episode}", SeriesName = "S", Season = season, Episode = episode,
        };

        var ordered = JellyfinClient.Ordered([Ep(0, 1), Ep(2, 1), Ep(1, 2), Ep(1, 1), Ep(null, 5)]);

        Assert.Equal(["1x1", "1x2", "2x1", "0x1", "x5"], ordered.Select(e => e.Name));
    }

    // ---- TMDb ----

    [Fact]
    public async Task Tmdb_AsksOncePerTitle_AndRemembersAcrossRuns()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"tmdb-{Guid.NewGuid():N}.json");
        var handler = new CountingHandler("""{ "id": 129, "original_language": "ja" }""");
        try
        {
            using (var tmdb = new TmdbClient("0123456789abcdef", cache, handler))
            {
                Assert.Equal("jpn", await tmdb.OriginalLanguageAsync("movie", "129", default));
                Assert.Equal("jpn", await tmdb.OriginalLanguageAsync("movie", "129", default));
            }

            using (var again = new TmdbClient("0123456789abcdef", cache, handler))
            {
                Assert.Equal("jpn", await again.OriginalLanguageAsync("movie", "129", default));
            }

            Assert.Equal(1, handler.Calls);
            Assert.Contains("api_key=0123456789abcdef", handler.LastUri);
        }
        finally
        {
            File.Delete(cache);
        }
    }

    [Fact]
    public async Task Tmdb_AReadAccessToken_IsSentAsABearerToken()
    {
        var handler = new CountingHandler("""{ "original_language": "fr" }""");
        var cache = Path.Combine(Path.GetTempPath(), $"tmdb-{Guid.NewGuid():N}.json");
        try
        {
            using var tmdb = new TmdbClient("eyJhbGciOiJIUzI1NiJ9.token", cache, handler);

            Assert.Equal("fra", await tmdb.OriginalLanguageAsync("tv", "1", default));
            Assert.Equal("Bearer", handler.LastAuthScheme);
            Assert.DoesNotContain("api_key", handler.LastUri);
        }
        finally
        {
            File.Delete(cache);
        }
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string LastUri { get; private set; } = string.Empty;

        public string? LastAuthScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
