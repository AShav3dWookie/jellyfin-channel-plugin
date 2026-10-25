using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Refreshes the Live TV guide when the plugin's settings are saved, and on request (after
/// channels are published), so changes appear without waiting for the daily refresh. The refresh
/// also rescans the store: Jellyfin asks for the channel list first.
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

    /// <summary>Queues Jellyfin's guide refresh.</summary>
    /// <returns>False if Jellyfin's Refresh Guide task can't be found.</returns>
    public bool Queue(string reason)
    {
        var worker = taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == RefreshGuideKey);
        if (worker is null)
        {
            logger.LogWarning("Could not find Jellyfin's Refresh Guide task; channels update at the next scheduled refresh");
            return false;
        }

        // Queued rather than executed: if a refresh is already running, Jellyfin runs this one
        // when it finishes. Executing a running task does nothing. Repeat requests while one is
        // queued are merged by Jellyfin.
        logger.LogInformation(
            worker.State == TaskState.Idle ? "{Reason}: refreshing the Live TV guide" : "{Reason}: guide refresh queued behind the one running",
            reason);
        taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        return true;
    }

    private void OnConfigurationSaved(object? sender, EventArgs e) => Queue("Settings saved");
}
