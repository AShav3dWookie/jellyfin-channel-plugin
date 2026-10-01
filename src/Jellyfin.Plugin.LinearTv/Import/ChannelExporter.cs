using System.Globalization;
using Jellyfin.Plugin.LinearTv.Configuration;

namespace Jellyfin.Plugin.LinearTv.Import;

/// <summary>
/// Writes the channel list as a <see cref="ChannelDocument"/>: the same format the importer reads,
/// so a channel list can be exported, edited (by hand or by AI) and imported back.
/// </summary>
internal static class ChannelExporter
{
    public static ChannelDocument Export(IEnumerable<ChannelDefinition> channels, LibraryCatalogue catalogue)
        => new()
        {
            Channels = channels
                .OrderBy(c => double.TryParse(c.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
                .ThenBy(c => c.Number, StringComparer.Ordinal)
                .Select(c => new ChannelSpec
                {
                    Number = c.Number,
                    Name = c.Name,
                    Shuffle = c.Shuffle,
                    Content = c.SourceIds.Select(id => Describe(id, catalogue)).ToList(),
                })
                .ToList(),
        };

    // Both the name and the id: the id makes a re-import on this server exact, and the name lets
    // another server (where ids differ) find the same item.
    private static ContentRef Describe(Guid id, LibraryCatalogue catalogue)
        => catalogue.Find(id) switch
        {
            null => new ContentRef { Id = id.ToString("N") },
            { Kind: ContentKind.Episode } e => new ContentRef
            {
                Id = id.ToString("N"),
                Type = e.Kind,
                Series = e.SeriesName,
                Season = e.Season,
                Episode = e.Episode,
            },
            var item => new ContentRef
            {
                Id = id.ToString("N"),
                Type = item.Kind,
                Title = item.Name,
                Year = item.Year,
            },
        };
}
