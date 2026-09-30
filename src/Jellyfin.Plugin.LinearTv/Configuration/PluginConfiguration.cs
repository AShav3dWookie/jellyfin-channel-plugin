using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LinearTv.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the channels, as defined on the settings page.
    /// </summary>
    public List<ChannelDefinition> Channels { get; set; } = [];

    /// <summary>
    /// Gets or sets the IANA timezone used for user-facing schedule rules, e.g. "Europe/London".
    /// </summary>
    /// <remarks>
    /// Deliberately explicit rather than inherited from the host. Jellyfin runs in a container
    /// that is usually UTC regardless of where the viewer is, so anything expressed as a local
    /// wall-clock rule ("this block starts at 20:00") must resolve against a configured zone.
    /// Internal scheduling remains UTC throughout; this value never participates in it.
    /// Empty means UTC.
    /// </remarks>
    public string ScheduleTimeZone { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many minutes must remain in a programme for a viewer to join it.
    /// Tuning in below this threshold skips to the next programme instead. Zero disables.
    /// </summary>
    public int JoinThresholdMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many hours of guide to publish ahead of now.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed the guide refresh interval (Jellyfin's default is 24 hours), or
    /// the guide runs nearly dry just before each refresh. Only affects what the guide shows:
    /// tuning always computes the live schedule directly.
    /// </remarks>
    public int ScheduleHorizonHours { get; set; } = 48;
}
