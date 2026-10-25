using System.Xml.Linq;
using Jellyfin.Plugin.LinearTv.Iptv;
using Jellyfin.Plugin.LinearTv.Scheduling;
using Jellyfin.Plugin.LinearTv.Store;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class IptvFeedsTests
{
    private static ChannelManifest Channel(string number, string name)
    {
        var channel = GuideTests.Channel(3, TimeSpan.FromHours(1));
        channel.Number = number;
        channel.Name = name;
        return channel;
    }

    // ---- the playlist ----

    [Fact]
    public void Playlist_ListsEachChannel_WithItsNumberAndAStreamUrlCarryingTheKey()
    {
        var m3u = IptvFeeds.Playlist([Channel("201", "Sci-Fi"), Channel("202", "Films")], "http://nas:8096/", "k+y");

        Assert.Equal(
            """
            #EXTM3U url-tvg="http://nas:8096/LinearTv/Iptv/guide.xml?ApiKey=k%2By"
            #EXTINF:-1 tvg-id="ltv201" tvg-chno="201" tvg-name="Sci-Fi" group-title="Linear TV",Sci-Fi
            http://nas:8096/LinearTv/Iptv/ltv201.ts?ApiKey=k%2By
            #EXTINF:-1 tvg-id="ltv202" tvg-chno="202" tvg-name="Films" group-title="Linear TV",Films
            http://nas:8096/LinearTv/Iptv/ltv202.ts?ApiKey=k%2By

            """.ReplaceLineEndings("\n"),
            m3u);
    }

    [Fact]
    public void Playlist_KeepsAQuoteOrLineBreakInANameFromBreakingTheEntry()
    {
        var m3u = IptvFeeds.Playlist([Channel("201", "The \"Best\"\nFilms")], "http://nas:8096", "key");

        Assert.Contains("tvg-name=\"The 'Best' Films\" group-title=\"Linear TV\",The \"Best\" Films\nhttp://", m3u);
    }

    // ---- the guide ----

    [Fact]
    public void Guide_MatchesThePlaylistsIds_AndCoversNowToAWeekAhead()
    {
        var now = ChannelClock.Epoch.AddDays(2000).AddMinutes(90);

        var tv = XDocument.Parse(System.Text.Encoding.UTF8.GetString(IptvFeeds.Guide([Channel("201", "Sci-Fi")], now))).Root!;

        Assert.Equal(["ltv201"], tv.Elements("channel").Select(c => (string)c.Attribute("id")!));
        Assert.Equal(["Sci-Fi", "201"], tv.Element("channel")!.Elements("display-name").Select(e => e.Value));
        var programmes = tv.Elements("programme").ToList();
        Assert.All(programmes, p => Assert.Equal("ltv201", (string)p.Attribute("channel")!));
        Assert.Equal(XmltvTime(now.AddMinutes(-90).AddHours(-2)), (string)programmes[0].Attribute("start")!);
        Assert.True(string.CompareOrdinal((string)programmes[^1].Attribute("stop")!, XmltvTime(now + IptvFeeds.GuideAhead)) >= 0);
    }

    [Fact]
    public void Guide_DescribesAnEpisode_WithXmltvsZeroBasedEpisodeNumbers()
    {
        var channel = Channel("201", "Sci-Fi");
        channel.Programmes[0] = new ManifestProgramme
        {
            Start = 0, Duration = channel.Programmes[0].Duration, Title = "Battlestar Galactica", EpisodeTitle = "33",
            Season = 1, Episode = 1, Year = 2004, Overview = "Every 33 minutes.", Genres = ["Science Fiction"], Rating = "15",
        };

        var tv = XDocument.Parse(System.Text.Encoding.UTF8.GetString(IptvFeeds.Guide([channel], ChannelClock.Epoch.AddMinutes(10)))).Root!;
        var episode = tv.Elements("programme").First(p => p.Element("title")!.Value == "Battlestar Galactica");

        Assert.Equal("33", episode.Element("sub-title")!.Value);
        Assert.Equal("Every 33 minutes.", episode.Element("desc")!.Value);
        Assert.Equal("2004", episode.Element("date")!.Value);
        Assert.Equal(["Science Fiction"], episode.Elements("category").Select(e => e.Value));
        Assert.Equal(["0.0.", "S01E01"], episode.Elements("episode-num").Select(e => e.Value));
        Assert.Equal("15", episode.Element("rating")!.Element("value")!.Value);
    }

    private static string XmltvTime(DateTime utc) => utc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";
}
