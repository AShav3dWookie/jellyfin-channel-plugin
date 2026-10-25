using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LinearTv.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the folder holding the built channels, as this server sees it. Each channel
    /// is a build folder in it, copied there from the PC that built it (docs/architecture.md).
    /// </summary>
    public string StorePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many minutes must remain in a programme for a viewer to join it.
    /// Tuning in below this threshold skips to the next programme instead. Zero disables.
    /// </summary>
    public int JoinThresholdMinutes { get; set; } = 5;

    // Channels and ScheduleTimeZone (to 0.4) are gone: channels are built, not configured here.
    // Saved settings that still contain them load fine; the XML serializer skips unknown elements.
}
