using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LinearTv.Api;

/// <summary>
/// The store of built channels: what's in it, and a rescan after publishing. Used by the settings
/// page and by the publish step on the PC that builds channels.
/// </summary>
[ApiController]
[Route("LinearTv/Store")]
[Authorize(Policy = "RequiresElevation")] // administrators only: Jellyfin.Api's Policies.RequiresElevation
public sealed class StoreController : ControllerBase
{
    /// <summary>Lists the channels in the store, as found by the last scan.</summary>
    /// <param name="rescan">Scan the store folder first.</param>
    /// <returns>The store folder and its channels.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get([FromQuery] bool rescan = false)
    {
        var store = HttpContext.RequestServices.GetRequiredService<ChannelStore>();
        var path = Plugin.Instance?.Configuration.StorePath ?? string.Empty;
        var channels = rescan ? store.Refresh(path) : store.Channels;
        return Ok(new
        {
            storePath = path,
            storeExists = Directory.Exists(path),
            channels = channels.Select(c => new
            {
                number = c.Manifest.Number,
                name = c.Manifest.Name,
                built = c.Manifest.Built,
                folder = Path.GetFileName(c.Directory),
                hours = Math.Round(TimeSpan.FromTicks(c.Manifest.CycleTicks).TotalHours, 2),
                programmes = c.Manifest.Programmes.Count,
                bytes = c.Manifest.Files.Sum(f => f.Bytes),
            }),
        });
    }

    /// <summary>Rescans the store and refreshes the Live TV guide, so newly published channels go on air.</summary>
    /// <returns>The channels now in the store.</returns>
    [HttpPost("Refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Refresh()
    {
        HttpContext.RequestServices.GetRequiredService<GuideRefreshTrigger>().Queue("Store refreshed");
        return Get(rescan: true);
    }
}
