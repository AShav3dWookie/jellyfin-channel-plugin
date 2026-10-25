using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.Scheduling;
using Jellyfin.Plugin.LinearTv.Store;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// Publishes the channels in the store to Jellyfin's Live TV: the channel list, the guide from
/// each channel's manifest, and the stream, read straight from the channel's files.
/// </summary>
/// <remarks>
/// Implements <see cref="ISupportsDirectStreamProvider"/> so that an open stream is our own
/// <see cref="LinearLiveStream"/> rather than Jellyfin's ExclusiveLiveStream: it plays on for as
/// long as someone watches, and it undoes Jellyfin's forced interlacing flag.
/// </remarks>
internal sealed class LinearTvService(
    ChannelStore store,
    IServerApplicationHost appHost,
    StreamRegistry streams,
    TimeProvider time,
    ILogger<LinearTvService> logger) : ILiveTvService, ISupportsDirectStreamProvider
{
    /// <summary>Most programmes one channel publishes per guide refresh.</summary>
    /// <remarks>
    /// Four weeks of two-hour films, so channels of normal programmes never reach it; it bounds
    /// channels of very short ones (a week of 30-second clips is 20,160 programmes).
    /// </remarks>
    internal const int MaxGuideProgrammes = 3000;

    public string Name => "Linear TV";

    public string HomePageUrl => "https://github.com/jellyfin/jellyfin";

    private static PluginConfiguration Config
        => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Linear TV plugin is not loaded.");

    // Jellyfin asks for the channel list at every guide refresh: the store is rescanned then, so
    // newly published channels appear with the guide.
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => Task.FromResult(store.Refresh(Config.StorePath).Select(c => new ChannelInfo
        {
            Id = c.Id,
            Name = c.Manifest.Name,
            Number = c.Manifest.Number,
            ChannelType = ChannelType.TV,
            HasImage = false,
        }));

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        if (store.Find(channelId) is not { } channel)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var programmes = GuideSlots(channel.Manifest, startDateUtc, endDateUtc).Select(slot => ToProgramInfo(channel, slot)).ToList();
        if (programmes.Count == MaxGuideProgrammes)
        {
            logger.LogInformation(
                "Guide for {Channel} stops at {Count} programmes, before {End:u}: its programmes are short",
                channel.Manifest.Name, programmes.Count, endDateUtc);
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programmes);
    }

    /// <summary>
    /// The guide for one channel: the range Jellyfin asks for, which its own "guide days" setting
    /// controls (7 days by default, 14 at most), up to <see cref="MaxGuideProgrammes"/>.
    /// </summary>
    internal static IEnumerable<GuideSlot> GuideSlots(ChannelManifest channel, DateTime startUtc, DateTime endUtc)
        => ChannelClock.Between(channel, startUtc, endUtc).Take(MaxGuideProgrammes);

    // Called when a client actually tunes in. Jellyfin prefers this over GetChannelStream
    // because the service implements ISupportsDirectStreamProvider.
    public Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var channel = FindChannel(channelId);
        var liveStream = new LinearLiveStream(CreateMediaSource(channel), () => OpenStream(channel), LocalBaseUrl, streams.Remove);
        streams.Add(liveStream);
        return Task.FromResult<ILiveStream>(liveStream);
    }

    // Required by ILiveTvService; while the method above exists, Jellyfin tunes through that.
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => Task.FromResult(CreateMediaSource(FindChannel(channelId)));

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => Task.FromResult<List<MediaSourceInfo>>([CreateMediaSource(FindChannel(channelId))]);

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

    /// <summary>
    /// The streams every channel carries, from its manifest: one video (H.265 since 1.0) and one AAC audio,
    /// numbered as the TS lists them. Declared so Jellyfin can decide on direct play without probing.
    /// </summary>
    internal static List<MediaStream> Streams(ChannelManifest channel) =>
    [
        new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = channel.Video.Codec,
            Profile = channel.Video.Profile,
            Level = channel.Video.Level,
            Width = channel.Video.Width,
            Height = channel.Video.Height,
            AverageFrameRate = (float)channel.Video.FrameRate,
            RealFrameRate = (float)channel.Video.FrameRate,
            BitRate = channel.Video.MaxBitRate,
            BitDepth = 8,
            PixelFormat = "yuv420p",
            IsInterlaced = false,
            IsAVC = channel.Video.Codec == "h264",
            AspectRatio = "16:9",
            IsDefault = true,
        },
        new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = channel.Audio.Codec,
            Profile = "LC",
            Channels = channel.Audio.Channels,
            ChannelLayout = channel.Audio.Channels == 2 ? "stereo" : null,
            SampleRate = channel.Audio.SampleRate,
            BitRate = channel.Audio.BitRate,
            IsDefault = true,
        },
    ];

    private static NotSupportedException RecordingNotSupported() => new("Linear TV channels cannot be recorded.");

    // Jellyfin fetches stream URLs itself, from inside its own host, so they must use the local
    // address rather than whatever the server is published as.
    private string LocalBaseUrl => appHost.GetApiUrlForLocalAccess(null, false);

    private StoredChannel FindChannel(string channelId)
        => store.Find(channelId) ?? throw new ArgumentException($"No Linear TV channel with id {channelId}.", nameof(channelId));

    /// <summary>The channel from the live position now. Called once per reader of the stream URL, so each joins at its own moment.</summary>
    private Stream OpenStream(StoredChannel channel)
    {
        var target = ChannelClock.Tune(channel.Manifest, time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(Math.Max(0, Config.JoinThresholdMinutes)));
        var (keyframe, offset) = channel.Index.AtOrBefore(target.PositionTicks);
        var programme = channel.Manifest.Programmes[target.ProgrammeIndex];
        logger.LogInformation(
            "{Channel}: playing {Programme} from {Offset:hh\\:mm\\:ss}",
            channel.Manifest.Name, Describe(programme), TimeSpan.FromTicks(Math.Max(0, keyframe - programme.Start)));
        return new StoreStream(channel.Files, offset, Convert.FromBase64String(channel.Manifest.Psi), channel.Manifest.Name, logger, time);
    }

    private MediaSourceInfo CreateMediaSource(StoredChannel channel) => new()
    {
        Id = channel.Id,
        Name = channel.Manifest.Name,
        // Replaced by LinearLiveStream with the plugin's own stream endpoint.
        Path = $"{LocalBaseUrl.TrimEnd('/')}/LinearTv/Stream/pending.ts",
        Protocol = MediaProtocol.Http,
        Container = "ts",
        IsRemote = false,
        IsInfiniteStream = true,
        RequiresOpening = true,
        RequiresClosing = true,
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        // The streams are declared below; probing would cost seconds per tune for nothing.
        SupportsProbing = false,
        // No -re: the stream paces itself (TsPacer) after an initial burst.
        ReadAtNativeFramerate = false,
        // Otherwise Jellyfin analyses 200 s of input before producing anything: on a paced
        // stream, 200 s of waiting. Must stay well inside StoreStream.InitialBurst.
        AnalyzeDurationMs = 5000,
        MediaStreams = Streams(channel.Manifest),
        DefaultAudioStreamIndex = 1,
    };

    private static ProgramInfo ToProgramInfo(StoredChannel channel, GuideSlot slot)
    {
        var p = slot.Programme;
        var image = channel.ImagePath(p);
        return new ProgramInfo
        {
            // Stable for a given slot, so each guide refresh updates rather than duplicates.
            Id = $"{channel.Id}-{slot.StartUtc.Ticks}",
            ChannelId = channel.Id,
            StartDate = slot.StartUtc,
            EndDate = slot.EndUtc,
            Name = p.Title,
            EpisodeTitle = p.EpisodeTitle,
            SeasonNumber = p.Season,
            EpisodeNumber = p.Episode,
            IsSeries = p.EpisodeTitle is not null || p.Season is not null,
            IsMovie = p.Movie,
            ProductionYear = p.Year,
            Overview = p.Overview,
            Genres = p.Genres ?? [],
            OfficialRating = p.Rating,
            ImagePath = image,
            HasImage = image is not null,
        };
    }

    private static string Describe(ManifestProgramme p)
        => p.Season is { } s ? $"{p.Title} S{s:00}E{p.Episode:00}" : p.Year is { } y ? $"{p.Title} ({y})" : p.Title;
}
