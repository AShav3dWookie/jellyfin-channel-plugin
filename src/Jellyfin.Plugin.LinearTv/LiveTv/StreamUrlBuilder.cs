using System.Globalization;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Builds the URL a tuned channel streams from: Jellyfin's own endpoint, remuxing the scheduled
/// item from the live offset. The plugin never touches media itself.
/// </summary>
internal static class StreamUrlBuilder
{
    /// <summary>A stream URL with a fresh play session id. Use this for every tune.</summary>
    public static string Build(string baseUrl, Guid itemId, long startTicks, string apiKey)
        => Build(baseUrl, itemId, startTicks, apiKey, Guid.NewGuid());

    /// <summary>A stream URL with an explicit play session id.</summary>
    /// <remarks>
    /// Two rules here were learned the hard way (docs/plan.md 4.7, 4.8):
    /// <list type="bullet">
    /// <item>The session id must be fresh on every tune. Jellyfin names remux output from
    /// MD5(path, user agent, device, session) and the start time is not part of it, so a reused
    /// session replays the first tune's offset.</item>
    /// <item>The key parameter is <c>ApiKey</c>. Jellyfin 12 rejects the legacy <c>api_key</c>,
    /// but the stream endpoint does not currently check auth at all, so the wrong name would
    /// work until the day it does.</item>
    /// </list>
    /// </remarks>
    public static string Build(string baseUrl, Guid itemId, long startTicks, string apiKey, Guid playSessionId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUrl.TrimEnd('/')}/Videos/{itemId:N}/stream?startTimeTicks={startTicks}&container=ts&videoCodec=copy&audioCodec=copy&PlaySessionId={playSessionId:N}&ApiKey={Uri.EscapeDataString(apiKey)}");
}
