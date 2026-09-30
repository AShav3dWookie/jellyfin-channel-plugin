using Jellyfin.Plugin.LinearTv.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class LinearLiveStreamTests
{
    private static LinearLiveStream Create(MediaSourceInfo source, Func<Stream>? open = null)
        => new(source, open ?? (() => new MemoryStream()), "http://127.0.0.1:8096/");

    [Fact]
    public void MediaSource_UndoesJellyfinsForcedInterlacing_KeepingGenuineValues()
    {
        var progressive = new MediaStream { Type = MediaStreamType.Video, Index = 0, IsInterlaced = false };
        var genuinelyInterlaced = new MediaStream { Type = MediaStreamType.Video, Index = 1, IsInterlaced = true };
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 2 };
        var stream = Create(new MediaSourceInfo { Id = "ch", MediaStreams = [progressive, genuinelyInterlaced, audio] });

        // What LiveTvMediaSourceProvider.Normalize() does to every third-party service's video.
        foreach (var s in stream.MediaSource.MediaStreams.Where(s => s.Type == MediaStreamType.Video))
        {
            s.IsInterlaced = true;
        }

        // Jellyfin's next read of the source, before any playback decision.
        var reread = stream.MediaSource;

        Assert.False(reread.MediaStreams[0].IsInterlaced);
        Assert.True(reread.MediaStreams[1].IsInterlaced);
    }

    [Fact]
    public void Path_PointsAtJellyfinsLiveStreamEndpointForThisStream()
    {
        var stream = Create(new MediaSourceInfo { Id = "ch", Path = "http://original", MediaStreams = [] });

        Assert.Equal($"http://127.0.0.1:8096/LiveTv/LiveStreamFiles/{stream.UniqueId}/stream.ts", stream.MediaSource.Path);
    }

    [Fact]
    public void GetStream_OpensANewStreamPerReader_AndCloseDisposesThem()
    {
        var opened = new List<MemoryStream>();
        var stream = Create(new MediaSourceInfo { Id = "ch", MediaStreams = [] }, () =>
        {
            var s = new MemoryStream();
            opened.Add(s);
            return s;
        });

        var first = stream.GetStream();
        var second = stream.GetStream();
        stream.Close();

        Assert.NotSame(first, second);
        Assert.All(opened, s => Assert.False(s.CanRead)); // disposed
    }
}
