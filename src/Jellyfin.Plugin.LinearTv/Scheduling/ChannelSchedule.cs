namespace Jellyfin.Plugin.LinearTv.Scheduling;

/// <summary>An item a channel can play, and how long it runs.</summary>
internal sealed record ScheduleEntry(Guid ItemId, long RuntimeTicks);

/// <summary>One programme slot in a channel's timeline.</summary>
internal sealed record ScheduledProgram(Guid ItemId, DateTime StartUtc, DateTime EndUtc);

/// <summary>What to play when a viewer tunes in: which programme, and how far into it.</summary>
internal sealed record TuneTarget(ScheduledProgram Program, long OffsetTicks);

/// <summary>
/// Computes a channel's timeline. Pure: no I/O, no clock of its own, no Jellyfin types.
/// </summary>
/// <remarks>
/// A channel loops its entries forever from a fixed epoch, so the programme on air at any
/// moment is arithmetic rather than stored state. It survives restarts and needs no storage.
/// The trade-off: changing a channel's content (adding an episode, say) shifts its whole
/// timeline, not just the future.
/// </remarks>
internal static class ChannelSchedule
{
    /// <summary>Every channel's timeline is anchored here.</summary>
    public static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Programmes overlapping [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), in order.</summary>
    public static IEnumerable<ScheduledProgram> Between(
        string channelId, IReadOnlyList<ScheduleEntry> entries, bool shuffle, DateTime fromUtc, DateTime toUtc)
    {
        var playable = entries.Where(e => e.RuntimeTicks > 0).ToList();
        var cycleTicks = playable.Sum(e => e.RuntimeTicks);
        if (cycleTicks == 0 || fromUtc >= toUtc)
        {
            yield break;
        }

        var cycle = FloorDiv((fromUtc - Epoch).Ticks, cycleTicks);
        var start = Epoch.AddTicks(cycle * cycleTicks);

        while (start < toUtc)
        {
            foreach (var entry in OrderFor(channelId, playable, shuffle, cycle))
            {
                var end = start.AddTicks(entry.RuntimeTicks);
                if (end > fromUtc && start < toUtc)
                {
                    yield return new ScheduledProgram(entry.ItemId, start, end);
                }

                start = end;
            }

            cycle++;
        }
    }

    /// <summary>The programme on air at <paramref name="nowUtc"/>, or null for an empty channel.</summary>
    public static ScheduledProgram? At(
        string channelId, IReadOnlyList<ScheduleEntry> entries, bool shuffle, DateTime nowUtc)
        => Between(channelId, entries, shuffle, nowUtc, nowUtc.AddTicks(1)).FirstOrDefault();

    /// <summary>
    /// Where a viewer tuning in at <paramref name="nowUtc"/> should start. With less than
    /// <paramref name="joinThreshold"/> left in the current programme, they start the next one
    /// from the top instead of catching the credits.
    /// </summary>
    public static TuneTarget? Resolve(
        string channelId, IReadOnlyList<ScheduleEntry> entries, bool shuffle, DateTime nowUtc, TimeSpan joinThreshold)
    {
        var current = At(channelId, entries, shuffle, nowUtc);
        if (current is null)
        {
            return null;
        }

        if (joinThreshold > TimeSpan.Zero && current.EndUtc - nowUtc < joinThreshold)
        {
            var next = At(channelId, entries, shuffle, current.EndUtc);
            return next is null ? null : new TuneTarget(next, 0);
        }

        return new TuneTarget(current, (nowUtc - current.StartUtc).Ticks);
    }

    private static IReadOnlyList<ScheduleEntry> OrderFor(
        string channelId, List<ScheduleEntry> entries, bool shuffle, long cycle)
    {
        // Two items can only alternate without repeating, which is what in-order already does.
        // Three or more also guarantees the swap below never moves a cycle's last item, which
        // the repeat check relies on.
        if (!shuffle || entries.Count < 3)
        {
            return entries;
        }

        var order = StableShuffle.Shuffle(entries, StableShuffle.Seed(channelId, cycle));

        // Each cycle is shuffled independently, so the last item of one cycle could open the
        // next and play twice in a row. Swap it out of first place.
        var previousLast = StableShuffle.Shuffle(entries, StableShuffle.Seed(channelId, cycle - 1))[^1];
        if (order[0] == previousLast)
        {
            (order[0], order[1]) = (order[1], order[0]);
        }

        return order;
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
