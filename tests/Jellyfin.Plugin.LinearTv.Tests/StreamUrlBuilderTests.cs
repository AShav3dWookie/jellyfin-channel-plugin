using Jellyfin.Plugin.LinearTv.LiveTv;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class StreamUrlBuilderTests
{
    private static readonly Guid Item = Guid.Parse("b95ed1c4-c8ba-4c14-b8b4-b26afd01ba26");

    [Fact]
    public void Build_ProducesTheVerifiedRemuxRequest()
    {
        var session = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var url = StreamUrlBuilder.Build("http://localhost:8096/", Item, 6_170_000_000, "k3y", session);

        Assert.Equal(
            "http://localhost:8096/Videos/b95ed1c4c8ba4c14b8b4b26afd01ba26/stream"
            + "?startTimeTicks=6170000000&container=ts&videoCodec=copy&audioCodec=copy"
            + "&PlaySessionId=11111111222233334444555555555555&ApiKey=k3y",
            url);
    }

    [Fact]
    public void Build_UsesApiKey_NotTheLegacyParameter()
    {
        // Jellyfin 12 rejects api_key; the stream endpoint just doesn't check yet (plan 4.7).
        var url = StreamUrlBuilder.Build("http://localhost:8096", Item, 0, "k3y");

        Assert.Contains("&ApiKey=k3y", url, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UsesAFreshPlaySessionEveryTime()
    {
        // A reused session replays the first tune's offset (plan 4.8).
        static string Session(string url) => url.Split("PlaySessionId=")[1].Split('&')[0];

        var first = StreamUrlBuilder.Build("http://localhost:8096", Item, 600, "k");
        var second = StreamUrlBuilder.Build("http://localhost:8096", Item, 600, "k");

        Assert.NotEqual(Session(first), Session(second));
    }

    [Fact]
    public void Build_EscapesTheKey()
    {
        var url = StreamUrlBuilder.Build("http://h", Item, 0, "a&b=c", Guid.Empty);

        Assert.EndsWith("&ApiKey=a%26b%3Dc", url, StringComparison.Ordinal);
    }
}
