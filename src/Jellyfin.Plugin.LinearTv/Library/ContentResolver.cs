using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.Scheduling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.Library;

/// <summary>
/// Turns a channel's sources (series, collections, libraries, videos) into the ordered list of
/// playable items its schedule loops through.
/// </summary>
internal sealed class ContentResolver(ILibraryManager libraryManager, ILogger<ContentResolver> logger)
{
    private static readonly BaseItemKind[] PlayableKinds =
        [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.MusicVideo, BaseItemKind.Video];

    /// <summary>The channel's content, in play order, each item once.</summary>
    /// <remarks>
    /// Items without a runtime are skipped. That is usually transient: Jellyfin indexes new items
    /// before probing them, so for a few seconds after a scan they have no runtime yet. They are
    /// picked up on the next resolve rather than excluded for good.
    /// </remarks>
    public IReadOnlyList<ScheduleEntry> Resolve(ChannelDefinition channel)
    {
        var seen = new HashSet<Guid>();
        var entries = new List<ScheduleEntry>();
        var unprobed = 0;

        foreach (var sourceId in channel.SourceIds)
        {
            foreach (var item in Expand(sourceId))
            {
                if (item.RunTimeTicks is not > 0)
                {
                    unprobed++;
                    continue;
                }

                if (seen.Add(item.Id))
                {
                    entries.Add(new ScheduleEntry(item.Id, item.RunTimeTicks.Value));
                }
            }
        }

        if (unprobed > 0)
        {
            logger.LogDebug("Channel {Channel}: skipped {Count} items with no runtime yet", channel.Name, unprobed);
        }

        return entries;
    }

    /// <summary>Looks up a scheduled item for guide metadata.</summary>
    public BaseItem? GetItem(Guid itemId) => libraryManager.GetItemById(itemId);

    private IEnumerable<BaseItem> Expand(Guid sourceId)
    {
        var source = libraryManager.GetItemById(sourceId);
        switch (source)
        {
            case null:
                logger.LogWarning("Channel source {Id} no longer exists in the library", sourceId);
                return [];

            // Series must be matched before Folder: a Series is a Folder.
            case Series series:
                return Descendants(series.Id, [BaseItemKind.Episode]);

            case BoxSet collection:
                return collection.GetLinkedChildren().SelectMany(child => child switch
                {
                    Series s => Descendants(s.Id, [BaseItemKind.Episode]),
                    Folder f => Descendants(f.Id, PlayableKinds),
                    _ when IsPlayable(child) => [child],
                    _ => [],
                });

            // A library or any other folder: everything playable beneath it.
            case Folder folder:
                return Descendants(folder.Id, PlayableKinds);

            case var item when IsPlayable(item):
                return [item];

            default:
                logger.LogWarning("Channel source {Name} ({Id}) is not something that can be played", source.Name, sourceId);
                return [];
        }
    }

    private IEnumerable<BaseItem> Descendants(Guid ancestorId, BaseItemKind[] kinds)
        => libraryManager
            .GetItemList(new InternalItemsQuery
            {
                AncestorIds = [ancestorId],
                IncludeItemTypes = kinds,
                Recursive = true,
                IsVirtualItem = false,
            })
            .OrderBy(i => (i as Episode)?.SeriesName ?? i.Name, StringComparer.OrdinalIgnoreCase)
            // Specials (season 0) and unnumbered seasons sort after the regular seasons.
            .ThenBy(i => i.ParentIndexNumber is null or 0 ? int.MaxValue : i.ParentIndexNumber.Value)
            .ThenBy(i => i.IndexNumber ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsPlayable(BaseItem item) => item is Video && !item.IsFolder;
}
