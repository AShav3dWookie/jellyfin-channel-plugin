using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Refreshes the Live TV guide whenever the plugin's settings are saved, so new or edited
/// channels appear without waiting for the daily refresh.
/// </summary>
internal sealed class GuideRefreshTrigger(ITaskManager taskManager, ILogger<GuideRefreshTrigger> logger) : IHostedService
{
    // Key of Jellyfin's built-in "Refresh Guide" scheduled task.
    private const string RefreshGuideKey = "RefreshGuide";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationSaved += OnConfigurationSaved;
        }
        else
        {
            logger.LogWarning("Linear TV plugin instance not available; the guide will not refresh on save");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationSaved -= OnConfigurationSaved;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationSaved(object? sender, EventArgs e)
    {
        var worker = taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == RefreshGuideKey);
        if (worker is null)
        {
            logger.LogWarning("Could not find Jellyfin's Refresh Guide task; channels update at the next scheduled refresh");
            return;
        }

        if (worker.State != TaskState.Idle)
        {
            logger.LogInformation("Guide refresh already running; channel changes may need another save or the next refresh");
            return;
        }

        logger.LogInformation("Settings saved: refreshing the Live TV guide");
        _ = taskManager.Execute(worker, new TaskOptions()).ContinueWith(
            t => logger.LogError(t.Exception, "Guide refresh after save failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
