using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.LinearTv.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.Api;

/// <summary>
/// Imports and exports channel lists in the format described in docs/channel-format.md, so
/// channels can be generated elsewhere (by hand, by script, or by an AI given the library) and
/// loaded without touching the settings page or restarting anything.
/// </summary>
[ApiController]
[Route("LinearTv/Channels")]
[Authorize(Policy = "RequiresElevation")] // administrators only: Jellyfin.Api's Policies.RequiresElevation
public sealed class ChannelImportController : ControllerBase
{
    /// <summary>Exports the current channels, with content named so it can be re-imported anywhere.</summary>
    /// <returns>The channel list as a channel document.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Export()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Linear TV plugin is not loaded.");
        var catalogue = HttpContext.RequestServices.GetRequiredService<LibraryCatalogueSource>().Build();
        return JsonContent(ChannelExporter.Export(plugin.Configuration.Channels, catalogue), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Imports channels. Every content reference must resolve, or nothing is saved. Saving
    /// refreshes the Live TV guide.
    /// </summary>
    /// <param name="dryRun">Report what would happen without saving anything.</param>
    /// <param name="mode">merge (default): replace channels with the same number and keep the rest.
    /// replace: the imported channels become the whole list.</param>
    /// <returns>A report of how each channel and content reference resolved. 200 if it can be (or
    /// was) saved, 422 if anything failed to resolve.</returns>
    [HttpPost("Import")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Import([FromQuery] bool dryRun = false, [FromQuery] string mode = "merge")
    {
        ImportMode importMode;
        switch (mode.Trim().ToLowerInvariant())
        {
            case "merge": importMode = ImportMode.Merge; break;
            case "replace": importMode = ImportMode.Replace; break;
            default: return Error($"mode must be merge or replace, not '{mode}'.");
        }

        ChannelDocument? document;
        try
        {
            // Read here rather than bound as a [FromBody] parameter: the format types are internal,
            // and binding would publish them as part of Jellyfin's API surface.
            document = await JsonSerializer.DeserializeAsync<ChannelDocument>(
                Request.Body, ChannelDocument.JsonOptions, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Error($"Not valid channel JSON: {ex.Message}");
        }

        if (document is null)
        {
            return Error("Empty request body.");
        }

        if (document.Version != 1)
        {
            return Error($"Unsupported format version {document.Version}; this plugin reads version 1.");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Linear TV plugin is not loaded.");
        var catalogue = HttpContext.RequestServices.GetRequiredService<LibraryCatalogueSource>().Build();

        var (report, imported) = ChannelImporter.Resolve(document, catalogue);
        report.DryRun = dryRun;
        report.Mode = importMode == ImportMode.Merge ? "merge" : "replace";

        if (report.Ok)
        {
            var merged = ChannelImporter.Merge(plugin.Configuration.Channels, imported, importMode, report);
            if (!dryRun)
            {
                plugin.Configuration.Channels = merged;
                plugin.UpdateConfiguration(plugin.Configuration); // also refreshes the guide
                report.Saved = true;

                HttpContext.RequestServices.GetRequiredService<ILogger<ChannelImportController>>().LogInformation(
                    "Imported {Count} channels ({Mode}); removed {Removed}",
                    imported.Count, report.Mode, report.Removed.Count);
            }
        }

        return JsonContent(report, report.Ok ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    // Serialised with the format's own options, so exports read exactly like imports.
    private ContentResult JsonContent(object value, int status) => new()
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), ChannelDocument.JsonOptions),
        ContentType = MediaTypeNames.Application.Json,
        StatusCode = status,
    };

    private ContentResult Error(string message) => JsonContent(new { error = message }, StatusCodes.Status400BadRequest);
}
