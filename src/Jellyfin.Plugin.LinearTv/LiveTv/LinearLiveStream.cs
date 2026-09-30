using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// An open channel stream. Does two things Jellyfin's own ExclusiveLiveStream cannot: keeps
/// playing from programme to programme, and undoes Jellyfin marking every third-party Live TV
/// stream interlaced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Continuity.</b> A plain remux URL ends when its programme ends, and clients then stop and
/// return to the menu (observed in the web client). Instead, <see cref="MediaSourceInfo.Path"/>
/// points at Jellyfin's own <c>/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts</c>, which serves
/// <see cref="GetStream"/>: a <see cref="ChannelStream"/> that runs on into each next programme.
/// </para>
/// <para>
/// <c>LiveTvMediaSourceProvider.Normalize()</c> runs
/// <c>if (service is not DefaultLiveTvService) … stream.IsInterlaced = true;</c> on every video
/// stream, a reasonable guess for broadcast tuners and wrong for library files. Browsers refuse
/// interlaced H.264, so it turned a cheap remux into a full decode and re-encode, with a
/// pointless deinterlace, of video that was never interlaced.
/// </para>
/// <para>
/// There is no supported way to opt out, but <c>MediaSourceManager</c> re-reads
/// <see cref="MediaSource"/> after <c>Normalize()</c> has run and before any playback decision is
/// made. So this getter puts each stream's real value back, as recorded when the source was
/// built from the library item's own probe. Genuinely interlaced content stays interlaced.
/// </para>
/// <para>
/// This depends on Jellyfin's internal call order. If that changes, the symptom is "Interlaced
/// video is not supported" reappearing as a transcode reason. docs/testing.md has the check.
/// The real fix belongs upstream: <c>Normalize()</c> should respect what a service declares.
/// </para>
/// </remarks>
internal sealed class LinearLiveStream : ILiveStream
{
    private readonly Dictionary<MediaStream, bool> _trueInterlacing;
    private readonly Func<Stream> _openStream;
    private readonly List<Stream> _opened = [];
    private MediaSourceInfo _mediaSource;

    /// <param name="mediaSource">The channel's source, as built for the current programme.</param>
    /// <param name="openStream">Opens a new continuous channel stream from the live position.</param>
    /// <param name="localBaseUrl">Jellyfin's own base URL, as reachable from inside its host.</param>
    public LinearLiveStream(MediaSourceInfo mediaSource, Func<Stream> openStream, string localBaseUrl)
    {
        _openStream = openStream;
        mediaSource.Path = $"{localBaseUrl.TrimEnd('/')}/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        _mediaSource = mediaSource;
        // Keyed by reference: these exact objects are what Normalize() mutates.
        _trueInterlacing = mediaSource.MediaStreams
            .Where(s => s.Type == MediaStreamType.Video)
            .ToDictionary<MediaStream, MediaStream, bool>(s => s, s => s.IsInterlaced, ReferenceEqualityComparer.Instance);
        OriginalStreamId = mediaSource.Id;
    }

    public MediaSourceInfo MediaSource
    {
        get
        {
            foreach (var stream in _mediaSource.MediaStreams)
            {
                if (_trueInterlacing.TryGetValue(stream, out var interlaced))
                {
                    stream.IsInterlaced = interlaced;
                }
            }

            return _mediaSource;
        }

        set => _mediaSource = value;
    }

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    public string TunerHostId => string.Empty;

    // Every tune is its own remux (a fresh play session), so there is nothing to share.
    public bool EnableStreamSharing => false;

    public string UniqueId { get; } = Guid.NewGuid().ToString("N");

    // Nothing to open up front: streams start when Jellyfin asks for them via GetStream().
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    public Task Close()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A new continuous stream from the live position now. Called by Jellyfin's LiveStreamFiles
    /// endpoint each time something reads <see cref="MediaSourceInfo.Path"/>, so every reader
    /// joins at its own moment.
    /// </summary>
    public Stream GetStream()
    {
        var stream = _openStream();
        lock (_opened)
        {
            _opened.Add(stream);
        }

        return stream;
    }

    public void Dispose()
    {
        lock (_opened)
        {
            foreach (var stream in _opened)
            {
                stream.Dispose();
            }

            _opened.Clear();
        }
    }
}
