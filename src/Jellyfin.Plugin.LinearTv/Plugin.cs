using Jellyfin.Plugin.LinearTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LinearTv;

/// <summary>
/// Presents library content as pseudo live TV channels.
/// </summary>
/// <remarks>
/// The plugin handles no media. It publishes channels and an EPG, and resolves a tuned channel
/// to a URL pointing back at Jellyfin's own <c>/Videos/{id}/stream</c> endpoint with the live
/// offset applied. See <c>docs/plan.md</c>.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">Configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Linear TV";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("437f1b36-c02d-4c02-9083-19440d7f243b");

    /// <inheritdoc />
    public override string Description =>
        "Turns library content into tunable pseudo live TV channels with a wall-clock schedule.";

    /// <summary>
    /// Raised after settings are saved. <see cref="BasePlugin{TConfigurationType}"/> exposes no
    /// such event in Jellyfin 12, so the plugin raises its own.
    /// </summary>
    public event EventHandler? ConfigurationSaved;

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // This path must match the file's case exactly — the deployment target is a
        // case-sensitive Linux filesystem.
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
