using System.Xml.Serialization;
using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Scheduling;
using Jellyfin.Plugin.LinearTv.Store;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class GuideTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 7, 54, 0, DateTimeKind.Utc);

    /// <summary>A built channel of <paramref name="count"/> programmes of one length, back to back.</summary>
    internal static ChannelManifest Channel(int count, TimeSpan length) => new()
    {
        Number = "201",
        Name = "Test",
        CycleTicks = count * length.Ticks,
        Programmes = [.. Enumerable.Range(0, count).Select(i => new ManifestProgramme { Title = $"P{i}", Start = i * length.Ticks, Duration = length.Ticks })],
    };

    // ---- the clock ----

    [Fact]
    public void Position_IsTimeSinceTheEpoch_ModTheCycle()
    {
        var channel = Channel(3, TimeSpan.FromHours(1));

        Assert.Equal(0, ChannelClock.Position(channel, ChannelClock.Epoch));
        Assert.Equal(TimeSpan.FromMinutes(90).Ticks, ChannelClock.Position(channel, ChannelClock.Epoch.AddMinutes(90)));
        Assert.Equal(TimeSpan.FromMinutes(30).Ticks, ChannelClock.Position(channel, ChannelClock.Epoch.AddHours(3).AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(150).Ticks, ChannelClock.Position(channel, ChannelClock.Epoch.AddMinutes(-30)));
    }

    [Fact]
    public void Tune_JoinsPartWay_OrStartsTheNextProgramme_NearTheEnd()
    {
        var channel = Channel(3, TimeSpan.FromHours(1));
        var threshold = TimeSpan.FromMinutes(5);

        var partWay = ChannelClock.Tune(channel, ChannelClock.Epoch.AddMinutes(70), threshold);
        Assert.Equal((TimeSpan.FromMinutes(70).Ticks, 1), (partWay.PositionTicks, partWay.ProgrammeIndex));

        var nearEnd = ChannelClock.Tune(channel, ChannelClock.Epoch.AddMinutes(118), threshold);
        Assert.Equal((TimeSpan.FromHours(2).Ticks, 2), (nearEnd.PositionTicks, nearEnd.ProgrammeIndex));

        // At the end of the loop, "next" is the first programme again.
        var lastMinutes = ChannelClock.Tune(channel, ChannelClock.Epoch.AddMinutes(178), threshold);
        Assert.Equal((0L, 0), (lastMinutes.PositionTicks, lastMinutes.ProgrammeIndex));
    }

    [Fact]
    public void Guide_RepeatsTheLoop_AndItsSlotsMatchWhatTuningPlays()
    {
        var channel = Channel(3, TimeSpan.FromHours(1));
        var from = ChannelClock.Epoch.AddHours(2.5);

        var slots = ChannelClock.Between(channel, from, from.AddHours(3)).ToList();

        Assert.Equal(["P2", "P0", "P1", "P2"], slots.Select(s => s.Programme.Title));
        Assert.Equal(ChannelClock.Epoch.AddHours(2), slots[0].StartUtc);
        foreach (var slot in slots)
        {
            Assert.Equal(slot.Programme.Title, channel.Programmes[ChannelClock.Tune(channel, slot.StartUtc.AddMinutes(1), TimeSpan.Zero).ProgrammeIndex].Title);
        }
    }

    [Theory]
    [InlineData(7)]   // Jellyfin's default
    [InlineData(14)]  // Jellyfin's maximum
    public void Guide_CoversTheWholeRangeJellyfinAsksFor(int days)
    {
        var start = Now.AddHours(-1);
        var end = start.AddDays(days);

        var slots = LinearTvService.GuideSlots(Channel(27, TimeSpan.FromMinutes(133)), start, end).ToList();

        Assert.True(slots[0].StartUtc <= start);
        Assert.True(slots[^1].EndUtc >= end, $"guide ends {slots[^1].EndUtc:u}, asked for {end:u}");
    }

    [Fact]
    public void Guide_OfVeryShortProgrammes_StopsAtTheCap()
    {
        var slots = LinearTvService.GuideSlots(Channel(6, TimeSpan.FromSeconds(30)), Now, Now.AddDays(7)).ToList();

        Assert.Equal(LinearTvService.MaxGuideProgrammes, slots.Count);
        Assert.True(slots[0].StartUtc <= Now, "the cap must cut the far end, never what's on now");
    }

    // ---- what Jellyfin is told about the stream ----

    [Fact]
    public void Streams_AreDeclaredFromTheManifest_Progressive()
    {
        var streams = LinearTvService.Streams(new ChannelManifest { Video = new VideoFormat { FrameRate = 23.976 } });

        Assert.Equal(("hevc", 1280, 720, false), (streams[0].Codec, streams[0].Width, streams[0].Height, streams[0].IsInterlaced));
        Assert.Equal(("Main", 123, false), (streams[0].Profile, streams[0].Level, streams[0].IsAVC));
        Assert.Equal(23.976f, streams[0].RealFrameRate);
        Assert.Equal(("aac", 2, 48000), (streams[1].Codec, streams[1].Channels, streams[1].SampleRate));
    }

    // ---- settings ----

    [Fact]
    public void SettingsFrom0x_StillLoad_WithTheirChannelsIgnored()
    {
        // As saved by 0.4. Jellyfin loads plugin settings with XmlSerializer.
        const string saved = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Channels>
                <ChannelDefinition>
                  <Id>85edb86340c01c11e2de9b25184c9adc</Id>
                  <Number>200</Number>
                  <Name>Spielberg</Name>
                  <SourceIds><guid>00000000-0000-0000-0000-000000000001</guid></SourceIds>
                  <Shuffle>false</Shuffle>
                </ChannelDefinition>
              </Channels>
              <ScheduleTimeZone />
              <JoinThresholdMinutes>7</JoinThresholdMinutes>
            </PluginConfiguration>
            """;

        using var reader = new StringReader(saved);
        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.Equal(7, config.JoinThresholdMinutes);
        Assert.Equal(string.Empty, config.StorePath);
    }
}
