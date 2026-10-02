using System.Text.Json;
using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.Library;
using Jellyfin.Plugin.LinearTv.Scheduling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Publishes the configured channels, their guide, and their streams to Jellyfin's Live TV.
/// </summary>
/// <remarks>
/// Implements <see cref="ISupportsDirectStreamProvider"/> so that an open stream is our own
/// <see cref="LinearLiveStream"/> rather than Jellyfin's ExclusiveLiveStream: it plays on from
/// programme to programme, and it undoes Jellyfin's forced interlacing flag.
/// </remarks>
internal sealed class LinearTvService(
    ContentResolver content,
    ApiKeyProvider apiKeys,
    IMediaSourceManager mediaSources,
    IServerApplicationHost appHost,
    IHttpClientFactory httpClientFactory,
    StreamRegistry streams,
    TimeProvider time,
    ILogger<LinearTvService> logger) : ILiveTvService, ISupportsDirectStreamProvider
{
    public string Name => "Linear TV";

    public string HomePageUrl => "https://github.com/jellyfin/jellyfin";

    private static PluginConfiguration Config
        => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Linear TV plugin is not loaded.");

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => Task.FromResult(Config.Channels.Select(c => new ChannelInfo
        {
            Id = c.Id,
            Name = c.Name,
            Number = c.Number,
            ChannelType = ChannelType.TV,
            HasImage = false,
        }));

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var channel = FindChannel(channelId);
        if (channel is null)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var entries = content.Resolve(channel);
        var programmes = GuideSlots(channel.Id, entries, channel.Shuffle, startDateUtc, endDateUtc)
            .Select(slot => ToProgramInfo(channel, slot))
            .ToList();

        if (programmes.Count == MaxGuideProgrammes)
        {
            logger.LogInformation(
                "Guide for {Channel} stops at {Count} programmes, before {End:u}: its programmes are short",
                channel.Name, programmes.Count, endDateUtc);
        }
        else
        {
            logger.LogDebug(
                "Guide for {Channel}: {Count} programmes from {Items} items",
                channel.Name, programmes.Count, entries.Count);
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programmes);
    }

    /// <summary>Most programmes one channel publishes per guide refresh.</summary>
    /// <remarks>
    /// Four weeks of two-hour films, so channels of normal programmes never reach it; it bounds
    /// channels of very short ones (a week of 30-second clips is 20,160 programmes).
    /// </remarks>
    internal const int MaxGuideProgrammes = 3000;

    /// <summary>
    /// The guide for one channel: the range Jellyfin asks for, which its own "guide days" setting
    /// controls (7 days by default, 14 at most), up to <see cref="MaxGuideProgrammes"/>. The guide
    /// is display only; tuning always computes the live schedule directly.
    /// </summary>
    internal static IEnumerable<ScheduledProgram> GuideSlots(
        string channelId, IReadOnlyList<ScheduleEntry> entries, bool shuffle, DateTime startUtc, DateTime endUtc)
        => ChannelSchedule.Between(channelId, entries, shuffle, startUtc, endUtc).Take(MaxGuideProgrammes);

    // Called when a client actually tunes in. Jellyfin prefers this over GetChannelStream
    // because the service implements ISupportsDirectStreamProvider.
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var source = await CreateMediaSourceAsync(channelId).ConfigureAwait(false);
        var planner = new Planner(this, FindChannel(channelId)!, content, apiKeys);

        var liveStream = new LinearLiveStream(source, OpenStream, LocalBaseUrl, streams.Remove);
        streams.Add(liveStream);
        return liveStream;

        Stream OpenStream()
        {
            var http = httpClientFactory.CreateClient();
            // A programme can run for hours; the stream is ended by cancellation, not a timeout.
            http.Timeout = Timeout.InfiniteTimeSpan;
            return new ChannelStream(planner, http, logger);
        }
    }

    // Required by ILiveTvService, but not used for tuning while the method above exists. Plays a
    // single programme only.
    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => await CreateMediaSourceAsync(channelId).ConfigureAwait(false);

    // Called when a client asks what it could play. Opening then calls GetChannelStream, which
    // recomputes the offset, so this one's URL is never streamed.
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => [await CreateMediaSourceAsync(channelId).ConfigureAwait(false)];

    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    // Recording is out of scope. Reads return nothing; writes refuse.
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => throw RecordingNotSupported();

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static NotSupportedException RecordingNotSupported() => new("Linear TV channels cannot be recorded.");

    private static ChannelDefinition? FindChannel(string channelId)
        => Config.Channels.FirstOrDefault(c => c.Id == channelId);

    // Jellyfin fetches stream URLs itself, from inside its own host, so they must use the local
    // address rather than whatever the server is published as.
    private string LocalBaseUrl => appHost.GetApiUrlForLocalAccess(null, false);

    private TuneTarget? ResolveNow(ChannelDefinition channel)
        => ChannelSchedule.Resolve(
            channel.Id,
            content.Resolve(channel),
            channel.Shuffle,
            time.GetUtcNow().UtcDateTime,
            TimeSpan.FromMinutes(Math.Max(0, Config.JoinThresholdMinutes)));

    private async Task<MediaSourceInfo> CreateMediaSourceAsync(string channelId)
    {
        var channel = FindChannel(channelId)
            ?? throw new ArgumentException($"No Linear TV channel with id {channelId}.", nameof(channelId));

        var target = ResolveNow(channel)
            ?? throw new InvalidOperationException($"Channel {channel.Name} has nothing to play.");

        var item = content.GetItem(target.Program.ItemId)
            ?? throw new InvalidOperationException($"Scheduled item {target.Program.ItemId} is no longer in the library.");

        // A single-programme URL. On the tuning path LinearLiveStream replaces it with the
        // continuous stream; it is only ever played through the unused GetChannelStream.
        var url = StreamUrlBuilder.Build(LocalBaseUrl, item.Id, target.OffsetTicks, await apiKeys.GetAsync().ConfigureAwait(false));

        // Declared from the programme on air now. A channel whose content mixes codecs or
        // resolutions will be described by whichever programme it was tuned on.
        var (streams, audioIndex) = RemuxedStreams(item);

        return new MediaSourceInfo
        {
            Id = channel.Id,
            Name = channel.Name,
            Path = url,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            IsRemote = false,
            IsInfiniteStream = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            // We declare the real streams below; probing would cost ~3s per tune for nothing.
            SupportsProbing = false,
            // No -re: the channel stream paces itself (TsPacer), after an initial burst. Jellyfin's
            // -re paced from the first frame, so a browser waited three whole segments
            // (~31 s measured) before it could start.
            ReadAtNativeFramerate = false,
            // Without this Jellyfin analyses 200 s of input (-analyzeduration 200M) before
            // producing anything. On a paced stream that is 200 s of waiting (166 s measured).
            // Must stay well inside ChannelStream.InitialBurst so the analysis never waits.
            AnalyzeDurationMs = 5000,
            MediaStreams = streams,
            DefaultAudioStreamIndex = audioIndex,
        };
    }

    /// <summary>
    /// The streams the remux actually carries: the first video and the default (else first)
    /// audio, renumbered from 0. Declared so Jellyfin can make a real direct-play-versus-
    /// transcode decision; left empty, Jellyfin substitutes dummies (docs/plan.md 4.6).
    /// </summary>
    private (List<MediaStream> Streams, int? AudioIndex) RemuxedStreams(BaseItem item)
    {
        var (video, audio) = PlayedStreams(item);

        var streams = new List<MediaStream>();
        int? audioIndex = null;
        foreach (var original in new[] { video, audio })
        {
            if (original is null)
            {
                continue;
            }

            // Copy: Jellyfin's Normalize() mutates the streams we return, and these objects
            // belong to the library item.
            var copy = JsonSerializer.Deserialize<MediaStream>(JsonSerializer.Serialize(original))!;
            copy.Index = streams.Count;
            if (copy.Type == MediaStreamType.Audio)
            {
                audioIndex = copy.Index;
            }

            streams.Add(copy);
        }

        return (streams, audioIndex);
    }

    /// <summary>
    /// The streams a remux of this item carries: the first video, and the default (else first)
    /// audio. The declared streams and the format comparison both use this, so they agree.
    /// </summary>
    private (MediaStream? Video, MediaStream? Audio) PlayedStreams(BaseItem item)
    {
        var all = mediaSources.GetStaticMediaSources(item, false, null).FirstOrDefault()?.MediaStreams ?? [];
        var video = all.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = all.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
            ?? all.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        return (video, audio);
    }

    /// <summary>Video and audio codec, e.g. "h264/aac"; null when there's no video information.</summary>
    private string? FormatOf(BaseItem item)
    {
        var (video, audio) = PlayedStreams(item);
        return video?.Codec is { Length: > 0 } videoCodec
            ? $"{videoCodec}/{audio?.Codec ?? "none"}".ToLowerInvariant()
            : null;
    }

    private ProgramInfo ToProgramInfo(ChannelDefinition channel, ScheduledProgram slot)
    {
        var programme = new ProgramInfo
        {
            // Stable for a given slot, so each guide refresh updates rather than duplicates.
            Id = $"{channel.Id}-{slot.StartUtc.Ticks}",
            ChannelId = channel.Id,
            StartDate = slot.StartUtc,
            EndDate = slot.EndUtc,
        };

        var item = content.GetItem(slot.ItemId);
        if (item is null)
        {
            programme.Name = "Unavailable";
            return programme;
        }

        programme.Overview = item.Overview;
        programme.OfficialRating = item.OfficialRating;
        programme.Genres = [.. item.Genres];
        programme.ProductionYear = item.ProductionYear;
        programme.ImagePath = PrimaryImagePath(item);
        programme.HasImage = programme.ImagePath is not null;

        if (item is Episode episode)
        {
            programme.Name = episode.SeriesName ?? item.Name;
            programme.EpisodeTitle = item.Name;
            programme.IsSeries = true;
            programme.SeriesId = episode.SeriesId.ToString("N");
            programme.SeasonNumber = episode.ParentIndexNumber;
            programme.EpisodeNumber = episode.IndexNumber;
        }
        else
        {
            programme.Name = item.Name;
            programme.IsMovie = item is Movie;
        }

        return programme;
    }

    private static string? PrimaryImagePath(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary, 0))
        {
            return item.GetImagePath(ImageType.Primary, 0);
        }

        // Episodes often have no image of their own; fall back to the series poster.
        return item is Episode { Series: { } series } && series.HasImage(ImageType.Primary, 0)
            ? series.GetImagePath(ImageType.Primary, 0)
            : null;
    }

    private static string Describe(BaseItem item)
        => item is Episode e ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00}" : item.Name;

    /// <summary>The schedule, as a <see cref="ChannelStream"/> sees it.</summary>
    private sealed class Planner(
        LinearTvService service, ChannelDefinition channel, ContentResolver content, ApiKeyProvider apiKeys) : IChannelPlanner
    {
        public string ChannelName => channel.Name;

        public TuneTarget? First() => service.ResolveNow(channel);

        // Re-resolves content at each boundary, so library changes are picked up as the channel
        // plays rather than only on the next tune.
        public ScheduledProgram? After(ScheduledProgram previous)
            => ChannelSchedule.At(channel.Id, content.Resolve(channel), channel.Shuffle, previous.EndUtc);

        public async Task<string> UrlForAsync(Guid itemId, long offsetTicks)
            => StreamUrlBuilder.Build(service.LocalBaseUrl, itemId, offsetTicks, await apiKeys.GetAsync().ConfigureAwait(false));

        public string Describe(Guid itemId)
            => content.GetItem(itemId) is { } item ? LinearTvService.Describe(item) : itemId.ToString("N");

        public string? FormatOf(Guid itemId)
            => content.GetItem(itemId) is { } item ? service.FormatOf(item) : null;
    }
}
