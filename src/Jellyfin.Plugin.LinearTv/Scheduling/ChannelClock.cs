using Jellyfin.Plugin.LinearTv.Store;

namespace Jellyfin.Plugin.LinearTv.Scheduling;

/// <summary>One programme's slot in the guide.</summary>
internal sealed record GuideSlot(ManifestProgramme Programme, DateTime StartUtc, DateTime EndUtc);

/// <summary>Where a viewer tuning in starts: a point in the channel file's timeline, and the programme there.</summary>
internal sealed record TuneTarget(long PositionTicks, int ProgrammeIndex);

/// <summary>
/// Maps a built channel onto the wall clock. Pure: no I/O, no clock of its own.
/// </summary>
/// <remarks>
/// A channel's file loops forever from a fixed epoch: at any moment, the point on air is
/// (now − epoch) mod the channel's cycle. That survives restarts and needs no stored state. A
/// rebuilt channel has a different cycle, so it comes back on air at a different point.
/// </remarks>
internal static class ChannelClock
{
    /// <summary>Every channel's loop is anchored here.</summary>
    public static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The point in the channel's timeline on air at <paramref name="nowUtc"/>.</summary>
    public static long Position(ChannelManifest channel, DateTime nowUtc)
        => FloorMod((nowUtc - Epoch).Ticks, channel.CycleTicks);

    /// <summary>Programmes overlapping [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), in order.</summary>
    public static IEnumerable<GuideSlot> Between(ChannelManifest channel, DateTime fromUtc, DateTime toUtc)
    {
        if (channel.CycleTicks <= 0 || channel.Programmes.Count == 0 || fromUtc >= toUtc)
        {
            yield break;
        }

        var loopStart = Epoch.AddTicks(FloorDiv((fromUtc - Epoch).Ticks, channel.CycleTicks) * channel.CycleTicks);
        for (; loopStart < toUtc; loopStart = loopStart.AddTicks(channel.CycleTicks))
        {
            foreach (var programme in channel.Programmes)
            {
                var start = loopStart.AddTicks(programme.Start);
                var end = loopStart.AddTicks(programme.End);
                if (start >= toUtc)
                {
                    yield break;
                }

                if (end > fromUtc)
                {
                    yield return new GuideSlot(programme, start, end);
                }
            }
        }
    }

    /// <summary>
    /// Where a viewer tuning in at <paramref name="nowUtc"/> starts. With less than
    /// <paramref name="joinThreshold"/> left of the programme on air, they start the next one from
    /// the top instead of catching its credits.
    /// </summary>
    public static TuneTarget Tune(ChannelManifest channel, DateTime nowUtc, TimeSpan joinThreshold)
    {
        var position = Position(channel, nowUtc);
        var index = ProgrammeAt(channel, position);
        var programme = channel.Programmes[index];
        if (joinThreshold > TimeSpan.Zero && programme.End - position < joinThreshold.Ticks)
        {
            index = (index + 1) % channel.Programmes.Count;
            return new TuneTarget(channel.Programmes[index].Start, index);
        }

        return new TuneTarget(position, index);
    }

    /// <summary>The index of the programme playing at <paramref name="position"/>.</summary>
    public static int ProgrammeAt(ChannelManifest channel, long position)
    {
        int low = 0, high = channel.Programmes.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (channel.Programmes[mid].Start <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static long FloorMod(long a, long b) => a - (FloorDiv(a, b) * b);
}
