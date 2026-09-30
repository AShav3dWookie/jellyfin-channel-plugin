using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LinearTv.Configuration;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class PluginIdentityTests
{
    private const string ConfigPageResource = "Jellyfin.Plugin.LinearTv.Configuration.configPage.html";

    // The plugin's constructor needs a live IApplicationPaths; Id and GetPages don't touch
    // any state, so an uninitialised instance is enough to read them.
    private static Plugin UninitialisedPlugin() =>
        (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    [Fact]
    public void ConfigPage_IsEmbeddedUnderTheExactNameThePluginRequests()
    {
        // Resource lookup is case-sensitive, and a mis-cased file silently drops out of the
        // build on Linux. This catches both on any OS, before a deploy does.
        var requested = UninitialisedPlugin().GetPages().Single().EmbeddedResourcePath;

        Assert.Equal(ConfigPageResource, requested);
        Assert.Contains(requested, typeof(Plugin).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void PluginId_MatchesBuildYamlAndConfigPage()
    {
        // The GUID lives in three places. Drift between them fails silently: a config page
        // that loads but never saves, or a repository update that installs alongside the
        // old version instead of replacing it.
        var id = UninitialisedPlugin().Id;

        var buildYaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build.yaml"));
        var yamlGuid = Regex.Match(buildYaml, @"^guid:\s*""?([0-9a-fA-F-]{36})""?", RegexOptions.Multiline);
        Assert.True(yamlGuid.Success, "No guid found in build.yaml");

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ConfigPageResource)!;
        var page = new StreamReader(stream).ReadToEnd();
        var pageGuid = Regex.Match(page, @"pluginId\s*=\s*'([0-9a-fA-F-]{36})'");
        Assert.True(pageGuid.Success, "No pluginId found in configPage.html");

        Assert.Equal(id, Guid.Parse(yamlGuid.Groups[1].Value));
        Assert.Equal(id, Guid.Parse(pageGuid.Groups[1].Value));
    }

    [Fact]
    public void Configuration_DefaultsToUtcAndAFiveMinuteJoinThreshold()
    {
        var config = new PluginConfiguration();

        // Empty means UTC: the container's ambient zone must never leak into scheduling.
        Assert.Equal(string.Empty, config.ScheduleTimeZone);
        Assert.Equal(5, config.JoinThresholdMinutes);
        Assert.Empty(config.Channels);

        // Must exceed Jellyfin's 24h guide refresh, or the guide runs dry before each refresh.
        Assert.True(config.ScheduleHorizonHours > 24);
    }
}
