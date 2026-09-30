namespace Jellyfin.Plugin.LinearTv.Configuration;

/// <summary>
/// A channel as the user defines it on the settings page.
/// </summary>
/// <remarks>
/// Public with settable properties because it is persisted by the plugin configuration's
/// XmlSerializer and round-tripped as JSON through the settings page.
/// </remarks>
public class ChannelDefinition
{
    /// <summary>Gets or sets the stable identifier. Also seeds the shuffle, so never reuse one.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the channel number shown in the guide, e.g. "101".</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name shown in the guide.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library items that supply this channel's content: series, collections,
    /// libraries, or individual videos. Played in this order unless <see cref="Shuffle"/> is set.
    /// </summary>
    public List<Guid> SourceIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether content is shuffled, reshuffling each loop.</summary>
    public bool Shuffle { get; set; }
}
