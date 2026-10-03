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

        // Queued rather than executed: if a refresh is already running (say, from the previous
        // save of several in a row), Jellyfin runs this one when it finishes. Executing a running
        // task does nothing, so a save landing mid-refresh used to wait for the daily refresh.
        // Repeat requests while one is queued are merged by Jellyfin.
        logger.LogInformation(
            worker.State == TaskState.Idle
                ? "Settings saved: refreshing the Live TV guide"
                : "Settings saved: guide refresh queued behind the one running");
        taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
    }
}
