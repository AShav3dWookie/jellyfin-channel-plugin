using Jellyfin.Plugin.LinearTv.Scheduling;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class ChannelScheduleTests
{
    private const string Channel = "test-channel";

    private static readonly DateTime E = ChannelSchedule.Epoch;

    private static readonly ScheduleEntry A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"), TimeSpan.FromMinutes(10).Ticks);
    private static readonly ScheduleEntry B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"), TimeSpan.FromMinutes(20).Ticks);
    private static readonly ScheduleEntry C = new(Guid.Parse("cccccccc-0000-0000-0000-000000000000"), TimeSpan.FromMinutes(30).Ticks);

    // A 10m, B 20m, C 30m: one loop is 60 minutes.
    private static readonly ScheduleEntry[] Abc = [A, B, C];

    private static DateTime At(int minutes, int seconds = 0) => E.AddMinutes(minutes).AddSeconds(seconds);

    [Fact]
    public void EmptyChannel_HasNothingOnAir()
    {
        Assert.Null(ChannelSchedule.At(Channel, [], false, At(5)));
        Assert.Empty(ChannelSchedule.Between(Channel, [], false, At(0), At(600)));
        Assert.Null(ChannelSchedule.Resolve(Channel, [], false, At(5), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 0, "aaaaaaaa")]      // first instant of the loop
    [InlineData(9, 59, "aaaaaaaa")]
    [InlineData(10, 0, "bbbbbbbb")]     // boundary belongs to the programme that starts there
    [InlineData(45, 0, "cccccccc")]
    [InlineData(60, 0, "aaaaaaaa")]     // wraps into the second loop
    [InlineData(6005, 0, "aaaaaaaa")]   // 100 loops later, 5 minutes in
    public void InOrder_PicksTheProgrammeOnAir(int minutes, int seconds, string expectedPrefix)
    {
        var onAir = ChannelSchedule.At(Channel, Abc, false, At(minutes, seconds));

        Assert.NotNull(onAir);
        Assert.StartsWith(expectedPrefix, onAir.ItemId.ToString());
    }

    [Fact]
    public void Resolve_OffsetIsTimeSinceTheProgrammeStarted()
    {
        // 15m into the loop: B started at 10m, so 5m in.
        var target = ChannelSchedule.Resolve(Channel, Abc, false, At(15), TimeSpan.Zero);

        Assert.NotNull(target);
        Assert.Equal(B.ItemId, target.Program.ItemId);
        Assert.Equal(At(10), target.Program.StartUtc);
        Assert.Equal(At(30), target.Program.EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, target.OffsetTicks);
    }

    [Fact]
    public void Resolve_NearTheEnd_SkipsToTheNextProgrammeFromTheTop()
    {
        // 28m: B has 2 minutes left, under a 5 minute threshold.
        var target = ChannelSchedule.Resolve(Channel, Abc, false, At(28), TimeSpan.FromMinutes(5));

        Assert.NotNull(target);
        Assert.Equal(C.ItemId, target.Program.ItemId);
        Assert.Equal(0, target.OffsetTicks);
    }

    [Fact]
    public void Resolve_ZeroThreshold_AlwaysJoinsInProgress()
    {
        var target = ChannelSchedule.Resolve(Channel, Abc, false, At(29, 59), TimeSpan.Zero);

        Assert.NotNull(target);
        Assert.Equal(B.ItemId, target.Program.ItemId);
        Assert.Equal(TimeSpan.FromSeconds(1199).Ticks, target.OffsetTicks);
    }

    [Fact]
    public void Between_ReturnsEveryOverlappingProgramme_InOrder()
    {
        // Window 25m..75m: B (10-30) overlaps the start, then C, A, and B (70-90) overlaps the end.
        var slots = ChannelSchedule.Between(Channel, Abc, false, At(25), At(75)).ToList();

        Assert.Equal([B.ItemId, C.ItemId, A.ItemId, B.ItemId], slots.Select(s => s.ItemId));
        Assert.Equal(At(10), slots[0].StartUtc);
        Assert.Equal(At(90), slots[^1].EndUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Between_IsContiguous_NoGapsOrOverlaps(bool shuffle)
    {
        var entries = Enumerable.Range(1, 7)
            .Select(i => new ScheduleEntry(Guid.NewGuid(), TimeSpan.FromMinutes(i * 3).Ticks))
            .ToArray();

        var slots = ChannelSchedule.Between(Channel, entries, shuffle, At(0), At(60 * 24 * 3)).ToList();

        Assert.NotEmpty(slots);
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.Equal(slots[i - 1].EndUtc, slots[i].StartUtc);
        }
    }

    [Fact]
    public void Between_WorksBeforeTheEpoch()
    {
        // One minute before the epoch is the last minute of loop -1, i.e. C's 29th minute.
        var target = ChannelSchedule.Resolve(Channel, Abc, false, E.AddMinutes(-1), TimeSpan.Zero);

        Assert.NotNull(target);
        Assert.Equal(C.ItemId, target.Program.ItemId);
        Assert.Equal(TimeSpan.FromMinutes(29).Ticks, target.OffsetTicks);
    }

    [Fact]
    public void ItemsWithoutARuntime_AreIgnored()
    {
        ScheduleEntry[] entries = [A, new(Guid.NewGuid(), 0), new(Guid.NewGuid(), -5), B];

        var slots = ChannelSchedule.Between(Channel, entries, false, At(0), At(30)).ToList();

        Assert.Equal([A.ItemId, B.ItemId], slots.Select(s => s.ItemId));
    }

    [Fact]
    public void Shuffle_EachLoopPlaysEveryItemExactlyOnce()
    {
        var entries = Enumerable.Range(0, 9).Select(_ => new ScheduleEntry(Guid.NewGuid(), TimeSpan.FromMinutes(10).Ticks)).ToArray();
        var loop = TimeSpan.FromMinutes(90);

        for (var n = 0; n < 20; n++)
        {
            var start = E + (loop * n);
            var played = ChannelSchedule.Between(Channel, entries, true, start, start + loop).Select(s => s.ItemId);

            Assert.Equal(entries.Select(e => e.ItemId).Order(), played.Order());
        }
    }

    [Fact]
    public void Shuffle_IsDeterministic()
    {
        var entries = Enumerable.Range(0, 9).Select(_ => new ScheduleEntry(Guid.NewGuid(), TimeSpan.FromMinutes(10).Ticks)).ToArray();

        var first = ChannelSchedule.Between(Channel, entries, true, At(0), At(900)).ToList();
        var second = ChannelSchedule.Between(Channel, entries, true, At(0), At(900)).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Shuffle_ReshufflesEachLoop()
    {
        var entries = Enumerable.Range(0, 9).Select(_ => new ScheduleEntry(Guid.NewGuid(), TimeSpan.FromMinutes(10).Ticks)).ToArray();

        var loop1 = ChannelSchedule.Between(Channel, entries, true, At(0), At(90)).Select(s => s.ItemId);
        var loop2 = ChannelSchedule.Between(Channel, entries, true, At(90), At(180)).Select(s => s.ItemId);

        Assert.NotEqual(loop1, loop2);
    }

    [Fact]
    public void Shuffle_NeverPlaysTheSameItemTwiceAcrossALoopBoundary()
    {
        // Few items make a repeat across the boundary likely if it isn't prevented.
        var entries = Enumerable.Range(0, 3).Select(_ => new ScheduleEntry(Guid.NewGuid(), TimeSpan.FromMinutes(10).Ticks)).ToArray();

        var slots = ChannelSchedule.Between(Channel, entries, true, At(0), At(30 * 500)).ToList();

        for (var i = 1; i < slots.Count; i++)
        {
            Assert.NotEqual(slots[i - 1].ItemId, slots[i].ItemId);
        }
    }

    [Theory]
    // Published FNV-1a 64-bit test vectors. Pins the hash as the fixed algorithm, not something
    // process-randomised like string.GetHashCode(), which would reshuffle channels on restart.
    [InlineData("", 0xcbf29ce484222325UL)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("foobar", 0x85944171f73967e8UL)]
    public void ShuffleSeed_UsesStableFnv1a(string input, ulong expected)
        => Assert.Equal(expected, StableShuffle.Fnv1a64(input));
}
