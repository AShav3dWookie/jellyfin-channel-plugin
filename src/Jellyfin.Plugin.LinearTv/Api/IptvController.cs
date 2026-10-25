using Jellyfin.Plugin.LinearTv.Iptv;
using Jellyfin.Plugin.LinearTv.LiveTv;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LinearTv.Api;

/// <summary>
/// The channels for IPTV apps: an M3U playlist, an XMLTV guide, and each channel's stream at a
/// fixed address. Alongside Live TV, not instead of it: same channels, same live position.
/// </summary>
/// <remarks>
/// <para>
/// An IPTV app can't log in to Jellyfin, so every address carries a Jellyfin API key as
/// <c>?ApiKey=</c>, the query parameter Jellyfin accepts without legacy authorization. The
/// playlist hands on whichever token fetched it.
/// </para>
/// <para>
/// The stream is the one Live TV plays (<see cref="LinearTvService.OpenStream"/>): a new reader
/// joins at the live position, gets a burst, then real time. Nothing runs in Jellyfin's
/// playback pipeline, so there is no transcoding: the app must decode the channel's H.265 and
/// AAC itself.
/// </para>
/// </remarks>
[ApiController]
[Route("LinearTv/Iptv")]
[Authorize]
public sealed class IptvController : ControllerBase
{
    /// <summary>The channels as an M3U playlist, with the guide's address in its header.</summary>
    /// <returns>The playlist.</returns>
    [HttpGet("channels.m3u")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Playlist()
    {
        var auth = await HttpContext.RequestServices.GetRequiredService<IAuthorizationContext>().GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (string.IsNullOrEmpty(auth.Token))
        {
            return Unauthorized();
        }

        var channels = Service.Channels().Select(c => c.Manifest);
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        return Content(IptvFeeds.Playlist(channels, baseUrl, auth.Token), "audio/x-mpegurl");
    }

    /// <summary>The guide in XMLTV form, a week ahead.</summary>
    /// <returns>The guide.</returns>
    [HttpGet("guide.xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Guide()
    {
        var now = HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        return File(IptvFeeds.Guide(Service.Channels().Select(c => c.Manifest).ToList(), now), "application/xml");
    }

    /// <summary>Streams a channel as MPEG-TS from the live position, until the app goes away.</summary>
    /// <param name="channelId">The channel's id, as the playlist gives it.</param>
    /// <returns>The channel's stream, or 404 if there is no such channel.</returns>
    [HttpGet("{channelId}.ts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Stream([FromRoute] string channelId)
        => Service.Lookup(channelId) is { } channel
            ? File(Service.OpenStream(channel), "video/mp2t", enableRangeProcessing: false)
            : NotFound();

    // Resolved here rather than injected: the service is internal, and ASP.NET only activates
    // controllers through a public constructor.
    private LinearTvService Service => HttpContext.RequestServices.GetRequiredService<LinearTvService>();
}
