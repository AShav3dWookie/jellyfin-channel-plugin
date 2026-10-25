using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Store;
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
        serviceCollection.AddSingleton<ChannelStore>();
        serviceCollection.AddSingleton<StreamRegistry>();

        // Jellyfin's LiveTvManager takes IEnumerable<ILiveTvService>; this adds ours alongside
        // the built-in one rather than replacing it. One instance, which the IPTV endpoints also use.
        serviceCollection.AddSingleton<LinearTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(services => services.GetRequiredService<LinearTvService>());

        // One instance: a hosted service (refresh on settings save) that the store endpoint also asks.
        serviceCollection.AddSingleton<GuideRefreshTrigger>();
        serviceCollection.AddSingleton<IHostedService>(services => services.GetRequiredService<GuideRefreshTrigger>());
    }
}
