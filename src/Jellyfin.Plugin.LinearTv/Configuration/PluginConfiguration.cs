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
    /// Tuning in below this threshold skips to the next programme instead. Zero, the default,
    /// always joins where the broadcast is, end credits included, like a real channel: a skip
    /// puts the viewer up to this many minutes ahead of the guide until they next tune.
    /// </summary>
    public int JoinThresholdMinutes { get; set; }

    // Channels and ScheduleTimeZone (to 0.4) are gone: channels are built, not configured here.
    // Saved settings that still contain them load fine; the XML serializer skips unknown elements.
}
