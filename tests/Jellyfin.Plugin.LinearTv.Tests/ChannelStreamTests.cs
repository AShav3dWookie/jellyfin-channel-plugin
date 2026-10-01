using System.Net;
using System.Text;
using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class ChannelStreamTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduledProgram Programme(char name, int slot)
        => new(ItemId(name), T0.AddMinutes(slot * 30), T0.AddMinutes((slot + 1) * 30));

    private static Guid ItemId(char name) => new(new string(name, 32));

    [Fact]
    public async Task PlaysTheFirstProgrammeFromItsOffset_ThenEachNextFromTheStart()
    {
        var planner = new FakePlanner(firstOffset: 5, Programme('a', 0), Programme('b', 1), Programme('c', 2));
        var server = new FakeServer(("a", "AAA"), ("b", "BB"), ("c", "CCC"));

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal("AAABBCCC", output);
        Assert.Equal(["a@5", "b@0", "c@0"], planner.Requested);
    }

    [Fact]
    public async Task SkipsAProgrammeThatFailsToOpen()
    {
        var planner = new FakePlanner(firstOffset: 0, Programme('a', 0), Programme('b', 1), Programme('c', 2));
        var server = new FakeServer(("a", "AAA"), ("c", "CCC")); // b is missing: 404

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal("AAACCC", output);
    }

    [Fact]
    public async Task GivesUpAfterRepeatedFailures_RatherThanSpinningForever()
    {
        var endless = Enumerable.Range(0, 100).Select(i => Programme('f', i)).ToArray();
        var planner = new FakePlanner(firstOffset: 0, endless);
        var server = new FakeServer(); // everything 404s

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal(string.Empty, output);
        Assert.Equal(5, planner.Requested.Count);
    }

    [Fact]
    public async Task AnEmptyChannelEndsImmediately()
    {
        var planner = new FakePlanner(firstOffset: 0);

        var output = await ReadAll(new ChannelStream(planner, new FakeServer().Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void SyncReadWorksToo()
    {
        var planner = new FakePlanner(firstOffset: 0, Programme('a', 0), Programme('b', 1));
        var server = new FakeServer(("a", "AA"), ("b", "B"));
        using var stream = new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false);

        using var reader = new StreamReader(stream, Encoding.ASCII);

        Assert.Equal("AAB", reader.ReadToEnd());
    }

    private static async Task<string> ReadAll(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return await reader.ReadToEndAsync();
        }
    }

    [Fact]
    public async Task EndsCleanly_AtTheFirstVideoCodecChange()
    {
        var planner = new FakePlanner(firstOffset: 5, Programme('a', 0), Programme('b', 1), Programme('c', 2), Programme('d', 3))
        {
            Formats = { ['c'] = "hevc/aac" },
        };
        var server = new FakeServer(("a", "AAA"), ("b", "BB"), ("c", "CCC"), ("d", "DD"));

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        // Same-format programmes play through; the stream ends before c rather than feed a player
        // set up for H.264 an HEVC programme. d is never reached.
        Assert.Equal("AAABB", output);
        Assert.Equal(["a@5", "b@0"], planner.Requested);
    }

    [Fact]
    public async Task EndsCleanly_AtAnAudioCodecChange()
    {
        var planner = new FakePlanner(firstOffset: 0, Programme('a', 0), Programme('b', 1))
        {
            Formats = { ['b'] = "h264/ac3" },
        };
        var server = new FakeServer(("a", "AAA"), ("b", "BB"));

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal("AAA", output);
    }

    [Fact]
    public async Task AnUnknownFormat_NeverStopsTheChannel()
    {
        var planner = new FakePlanner(firstOffset: 0, Programme('a', 0), Programme('b', 1), Programme('c', 2))
        {
            Formats = { ['b'] = null },
        };
        var server = new FakeServer(("a", "AAA"), ("b", "BB"), ("c", "CCC"));

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal("AAABBCCC", output);
    }

    [Fact]
    public async Task TheTunedFormat_ComesFromTheFirstProgrammeThatActuallyOpened()
    {
        // a is HEVC but fails to open, so the player is set up by b, which is H.264.
        var planner = new FakePlanner(firstOffset: 0, Programme('a', 0), Programme('b', 1), Programme('c', 2))
        {
            Formats = { ['a'] = "hevc/aac" },
        };
        var server = new FakeServer(("b", "BB"), ("c", "CCC"));

        var output = await ReadAll(new ChannelStream(planner, server.Client, NullLogger.Instance, spliceTimestamps: false));

        Assert.Equal("BBCCC", output);
    }

    /// <summary>A schedule of fixed programmes, played in order.</summary>
    private sealed class FakePlanner(long firstOffset, params ScheduledProgram[] programmes) : IChannelPlanner
    {
        /// <summary>Gets formats by programme name; anything not listed is "h264/aac".</summary>
        public Dictionary<char, string?> Formats { get; } = [];

        public List<string> Requested { get; } = [];

        public string? FormatOf(Guid itemId)
            => Formats.TryGetValue(itemId.ToString("N")[0], out var format) ? format : "h264/aac";

        public string ChannelName => "Test";

        public TuneTarget? First() => programmes.Length == 0 ? null : new TuneTarget(programmes[0], firstOffset);

        public ScheduledProgram? After(ScheduledProgram previous)
        {
            var i = Array.IndexOf(programmes, previous);
            return i + 1 < programmes.Length ? programmes[i + 1] : null;
        }

        public Task<string> UrlForAsync(Guid itemId, long offsetTicks)
        {
            var name = itemId.ToString("N")[0];
            Requested.Add($"{name}@{offsetTicks}");
            return Task.FromResult($"http://jellyfin.test/{name}");
        }

        public string Describe(Guid itemId) => itemId.ToString("N")[..1];
    }

    /// <summary>Serves a fixed body per path; anything else is a 404.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly (string Path, string Body)[] _routes;

        public FakeServer(params (string Path, string Body)[] routes) => _routes = routes;

        public HttpClient Client => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var route = _routes.FirstOrDefault(r => r.Path == path);
            return Task.FromResult(route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route.Body, Encoding.ASCII) });
        }
    }
}
