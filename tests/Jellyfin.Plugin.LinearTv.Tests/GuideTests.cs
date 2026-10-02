using System.Xml.Serialization;
using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Scheduling;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class GuideTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 7, 54, 0, DateTimeKind.Utc);

    private static ScheduleEntry[] Films(int count, TimeSpan length)
        => [.. Enumerable.Range(0, count).Select(_ => new ScheduleEntry(Guid.NewGuid(), length.Ticks))];

    [Theory]
    [InlineData(7)]   // Jellyfin's default
    [InlineData(14)]  // Jellyfin's maximum
    public void Guide_CoversTheWholeRangeJellyfinAsksFor(int days)
    {
        // As Jellyfin asks: from an hour ago, for its "guide days".
        var start = Now.AddHours(-1);
        var end = start.AddDays(days);

        // The Spielberg channel's shape: 27 films of about two hours.
        var slots = LinearTvService.GuideSlots("spielberg", Films(27, TimeSpan.FromMinutes(133)), false, start, end).ToList();

        Assert.True(slots[0].StartUtc <= start);
        Assert.True(slots[^1].EndUtc >= end, $"guide ends {slots[^1].EndUtc:u}, asked for {end:u}");
    }

    [Fact]
    public void Guide_OfVeryShortProgrammes_StopsAtTheCap()
    {
        // A week of 30-second clips would be 20,160 programmes.
        var slots = LinearTvService.GuideSlots("clips", Films(6, TimeSpan.FromSeconds(30)), true, Now, Now.AddDays(7)).ToList();

        Assert.Equal(LinearTvService.MaxGuideProgrammes, slots.Count);
        Assert.True(slots[0].StartUtc <= Now, "the cap must cut the far end, never what's on now");
    }

    [Fact]
    public void Cap_IsNeverReached_ByChannelsOfNormalProgrammes()
    {
        // Half-hour episodes for Jellyfin's maximum 14 days.
        var slots = LinearTvService.GuideSlots("telly", Films(40, TimeSpan.FromMinutes(30)), false, Now, Now.AddDays(14)).Count();

        Assert.True(slots < LinearTvService.MaxGuideProgrammes);
    }

    [Fact]
    public void SettingsSavedBefore04_StillLoad_WithTheRemovedHorizonIgnored()
    {
        // As saved by 0.3 and earlier. Jellyfin loads plugin settings with XmlSerializer.
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
              <JoinThresholdMinutes>5</JoinThresholdMinutes>
              <ScheduleHorizonHours>48</ScheduleHorizonHours>
            </PluginConfiguration>
            """;

        using var reader = new StringReader(saved);
        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.Equal("Spielberg", Assert.Single(config.Channels).Name);
        Assert.Equal(5, config.JoinThresholdMinutes);
    }
}
