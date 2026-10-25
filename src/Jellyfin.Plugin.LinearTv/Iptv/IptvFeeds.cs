using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Scheduling;
using Jellyfin.Plugin.LinearTv.Store;

namespace Jellyfin.Plugin.LinearTv.Iptv;

/// <summary>
/// The channels as an IPTV app reads them: an M3U playlist of stream URLs and an XMLTV guide.
/// Pure: the caller supplies the channels, the addresses and the time.
/// </summary>
/// <remarks>
/// Why: Jellyfin's own clients have no one-press channel change (jellyfin-web maps CH+/CH− to
/// chapters; the Android TV app has a channel picker, not channel up/down). IPTV apps do, and
/// the channels are already plain MPEG-TS, so an IPTV app can play them directly, with no
/// Jellyfin playback session per channel change.
/// </remarks>
internal static class IptvFeeds
{
    /// <summary>How far back the guide starts, so the programme on air is always in it.</summary>
    public static readonly TimeSpan GuideBehind = TimeSpan.FromHours(3);

    /// <summary>How far ahead the guide runs: Jellyfin's own default guide length.</summary>
    public static readonly TimeSpan GuideAhead = TimeSpan.FromDays(7);

    /// <summary>
    /// The M3U playlist. <paramref name="apiKey"/> goes into every URL: an IPTV app can only
    /// authenticate through the address it's given.
    /// </summary>
    /// <param name="channels">The channels, in channel number order: the order CH+/CH− steps through.</param>
    /// <param name="baseUrl">Jellyfin's base URL as the app reaches it.</param>
    /// <param name="apiKey">The Jellyfin API key the app uses.</param>
    public static string Playlist(IEnumerable<ChannelManifest> channels, string baseUrl, string apiKey)
    {
        var root = baseUrl.TrimEnd('/');
        var key = Uri.EscapeDataString(apiKey);
        var m3u = new StringBuilder();
        m3u.Append(CultureInfo.InvariantCulture, $"#EXTM3U url-tvg=\"{root}/LinearTv/Iptv/guide.xml?ApiKey={key}\"\n");
        foreach (var channel in channels)
        {
            var id = StoredChannel.IdFor(channel.Number);
            m3u.Append(CultureInfo.InvariantCulture, $"#EXTINF:-1 tvg-id=\"{id}\" tvg-chno=\"{Attribute(channel.Number)}\" tvg-name=\"{Attribute(channel.Name)}\" group-title=\"Linear TV\",{OneLine(channel.Name)}\n");
            m3u.Append(CultureInfo.InvariantCulture, $"{root}/LinearTv/Iptv/{id}.ts?ApiKey={key}\n");
        }

        return m3u.ToString();
    }

    /// <summary>The XMLTV guide, from <see cref="GuideBehind"/> before <paramref name="nowUtc"/> to <see cref="GuideAhead"/> after it.</summary>
    /// <remarks>Channel ids match the playlist's <c>tvg-id</c>, which is how the app pairs them.</remarks>
    public static byte[] Guide(IReadOnlyList<ChannelManifest> channels, DateTime nowUtc)
    {
        using var output = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var xml = XmlWriter.Create(output, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("tv");
            xml.WriteAttributeString("generator-info-name", "Linear TV");

            foreach (var channel in channels)
            {
                xml.WriteStartElement("channel");
                xml.WriteAttributeString("id", StoredChannel.IdFor(channel.Number));
                xml.WriteElementString("display-name", channel.Name);
                xml.WriteElementString("display-name", channel.Number);
                xml.WriteEndElement();
            }

            foreach (var channel in channels)
            {
                foreach (var slot in LinearTvService.GuideSlots(channel, nowUtc - GuideBehind, nowUtc + GuideAhead))
                {
                    WriteProgramme(xml, StoredChannel.IdFor(channel.Number), slot);
                }
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return output.ToArray();
    }

    private static void WriteProgramme(XmlWriter xml, string channelId, GuideSlot slot)
    {
        var p = slot.Programme;
        xml.WriteStartElement("programme");
        xml.WriteAttributeString("start", XmltvTime(slot.StartUtc));
        xml.WriteAttributeString("stop", XmltvTime(slot.EndUtc));
        xml.WriteAttributeString("channel", channelId);
        xml.WriteElementString("title", p.Title);
        if (p.EpisodeTitle is { } episodeTitle)
        {
            xml.WriteElementString("sub-title", episodeTitle);
        }

        if (p.Overview is { } overview)
        {
            xml.WriteElementString("desc", overview);
        }

        if (p.Year is { } year)
        {
            xml.WriteElementString("date", year.ToString(CultureInfo.InvariantCulture));
        }

        if (p.Movie)
        {
            xml.WriteElementString("category", "Movie");
        }

        foreach (var genre in p.Genres ?? [])
        {
            xml.WriteElementString("category", genre);
        }

        if (p.Season is { } season && p.Episode is { } episode)
        {
            // xmltv_ns counts from zero.
            xml.WriteStartElement("episode-num");
            xml.WriteAttributeString("system", "xmltv_ns");
            xml.WriteString(string.Create(CultureInfo.InvariantCulture, $"{season - 1}.{episode - 1}."));
            xml.WriteEndElement();
            xml.WriteStartElement("episode-num");
            xml.WriteAttributeString("system", "onscreen");
            xml.WriteString(string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}"));
            xml.WriteEndElement();
        }

        if (p.Rating is { } rating)
        {
            xml.WriteStartElement("rating");
            xml.WriteElementString("value", rating);
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static string XmltvTime(DateTime utc) => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    // M3U has no escaping: a quote would end the attribute, a line break the entry.
    private static string Attribute(string value) => OneLine(value).Replace('"', '\'');

    private static string OneLine(string value) => value.ReplaceLineEndings(" ");
}
