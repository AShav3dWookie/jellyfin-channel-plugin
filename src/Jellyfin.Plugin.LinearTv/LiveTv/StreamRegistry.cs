using System.Collections.Concurrent;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// The channel streams currently open, by their unguessable unique id, so the plugin's stream
/// endpoint can serve them.
/// </summary>
internal sealed class StreamRegistry
{
    private readonly ConcurrentDictionary<string, LinearLiveStream> _streams = new(StringComparer.OrdinalIgnoreCase);

    public void Add(LinearLiveStream stream) => _streams[stream.UniqueId] = stream;

    public void Remove(LinearLiveStream stream) => _streams.TryRemove(new(stream.UniqueId, stream));

    public LinearLiveStream? Find(string uniqueId) => _streams.GetValueOrDefault(uniqueId);
}
