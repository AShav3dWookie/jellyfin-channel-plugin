using Jellyfin.Plugin.LinearTv.Import;
using Jellyfin.Plugin.LinearTv.Library;
using Jellyfin.Plugin.LinearTv.LiveTv;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.LinearTv;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ContentResolver>();
        serviceCollection.AddSingleton<ApiKeyProvider>();
        serviceCollection.AddSingleton<StreamRegistry>();
        serviceCollection.AddSingleton<LibraryCatalogueSource>();

        // Jellyfin's LiveTvManager takes IEnumerable<ILiveTvService>; this adds ours alongside
        // the built-in one rather than replacing it.
        serviceCollection.AddSingleton<ILiveTvService, LinearTvService>();

        serviceCollection.AddSingleton<IHostedService, GuideRefreshTrigger>();
    }
}
