using Jellyfin.Plugin.LinearTv.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LinearTv.Api;

/// <summary>
/// Serves open channel streams. This is the plugin's own version of Jellyfin's
/// <c>/LiveTv/LiveStreamFiles/{id}/stream.ts</c>.
/// </summary>
/// <remarks>
/// <para>
/// Why not use Jellyfin's endpoint: it wraps the stream in a ProgressiveFileStream, which on
/// end-of-stream keeps retrying for 30 seconds before giving up. That suits a file that is still
/// being written. Here it meant a channel that ends cleanly at a format change (see
/// <see cref="ChannelStream"/>) only stopped 30 s later. Browsers buffer as little as 6 s ahead
/// (jellyfin-web, Chrome/Edge/Firefox on fast connections), so viewers would watch a frozen last
/// frame for ~24 s. Served directly, the end reaches the player straight away.
/// </para>
/// <para>
/// Anonymous, like Jellyfin's own endpoint: Jellyfin's ffmpeg and its stream proxy fetch this
/// URL without credentials. Access depends on knowing the stream's unique id, an unguessable
/// GUID that exists only while the stream is open.
/// </para>
/// </remarks>
[ApiController]
[Route("LinearTv/Stream")]
[AllowAnonymous]
public sealed class ChannelStreamController : ControllerBase
{
    /// <summary>Streams an open channel as MPEG-TS until it ends or the reader goes away.</summary>
    /// <param name="streamId">The stream's unique id.</param>
    /// <returns>The channel's MPEG-TS stream, or 404 if no such stream is open.</returns>
    [HttpGet("{streamId}.ts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get([FromRoute] string streamId)
    {
        // Resolved here rather than injected: the registry is internal, and ASP.NET only activates
        // controllers through a public constructor.
        var registry = HttpContext.RequestServices.GetRequiredService<StreamRegistry>();
        return registry.Find(streamId) is { } stream
            ? File(stream.GetStream(), "video/mp2t", enableRangeProcessing: false)
            : NotFound();
    }
}
