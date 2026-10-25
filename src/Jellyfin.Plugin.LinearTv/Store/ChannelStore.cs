using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.Store;

/// <summary>A channel in the store: its newest complete build.</summary>
internal sealed class StoredChannel(string directory, ChannelManifest manifest)
{
    private readonly Lazy<KeyframeIndex> _index = new(() => KeyframeIndex.ReadFile(Path.Combine(directory, manifest.Index)));

    /// <summary>Gets the id Jellyfin knows the channel by: from its number, so a rebuilt channel keeps its place.</summary>
    public string Id { get; } = IdFor(manifest.Number);

    public string Directory { get; } = directory;

    public ChannelManifest Manifest { get; } = manifest;

    /// <summary>Gets the keyframe index, read the first time a viewer tunes in.</summary>
    public KeyframeIndex Index => _index.Value;

    /// <summary>The channel's stream files in order, with full paths.</summary>
    public IReadOnlyList<(string Path, long Bytes)> Files => Manifest.Files.Select(f => (System.IO.Path.Combine(Directory, f.Name), f.Bytes)).ToList();

    public static string IdFor(string number)
        => "ltv" + string.Concat(number.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    /// <summary>A programme's artwork as a full path, if it has any and the file is there.</summary>
    public string? ImagePath(ManifestProgramme programme)
        => programme.Image is { } image && File.Exists(System.IO.Path.Combine(Directory, image)) ? System.IO.Path.Combine(Directory, image) : null;
}

/// <summary>
/// The channels in the store folder: one per channel number, the newest complete build of each.
/// A build folder is <c>&lt;number&gt;-&lt;UTC time&gt;</c>; it counts once it has a
/// <c>channel.json</c>, which publishing copies last, so a half-copied build is never on air.
/// </summary>
internal sealed class ChannelStore(ILogger<ChannelStore> logger)
{
    private volatile IReadOnlyList<StoredChannel> _channels = [];
    private string? _scannedPath;

    /// <summary>Gets the channels found by the last <see cref="Refresh"/>, in channel number order.</summary>
    public IReadOnlyList<StoredChannel> Channels => _channels;

    public StoredChannel? Find(string id) => _channels.FirstOrDefault(c => c.Id == id);

    /// <summary>Scans the store folder again. Builds already found and unchanged are kept, with their loaded indexes.</summary>
    public IReadOnlyList<StoredChannel> Refresh(string storePath)
    {
        if (string.IsNullOrWhiteSpace(storePath) || !System.IO.Directory.Exists(storePath))
        {
            if (_scannedPath != storePath)
            {
                logger.LogWarning("Linear TV store folder '{Path}' doesn't exist; no channels", storePath);
                _scannedPath = storePath;
            }

            return _channels = [];
        }

        _scannedPath = storePath;
        var previous = _channels.ToDictionary(c => c.Directory, StringComparer.Ordinal);
        var newest = new Dictionary<string, (DateTime Built, StoredChannel Channel)>(StringComparer.Ordinal);
        foreach (var directory in System.IO.Directory.EnumerateDirectories(storePath))
        {
            var manifestPath = Path.Combine(directory, ChannelManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            StoredChannel? channel = previous.GetValueOrDefault(directory);
            if (channel is null)
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<ChannelManifest>(File.ReadAllText(manifestPath), ChannelManifest.JsonOptions);
                    if (manifest is null || manifest.Format != ChannelManifest.CurrentFormat || manifest.Programmes.Count == 0 || manifest.CycleTicks <= 0)
                    {
                        logger.LogWarning("Skipping {Directory}: not a channel this plugin can play (format {Format})", directory, manifest?.Format);
                        continue;
                    }

                    channel = new StoredChannel(directory, manifest);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Skipping {Directory}: can't read its {File}", directory, ChannelManifest.FileName);
                    continue;
                }
            }

            var number = channel.Manifest.Number;
            if (!newest.TryGetValue(number, out var current) || channel.Manifest.Built > current.Built)
            {
                newest[number] = (channel.Manifest.Built, channel);
            }
        }

        var channels = newest.Values
            .Select(v => v.Channel)
            .OrderBy(c => double.TryParse(c.Manifest.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
            .ThenBy(c => c.Manifest.Number, StringComparer.Ordinal)
            .ToList();

        // Read new channels' keyframe indexes now, in the background, rather than on their first
        // tune: an 86-hour channel's is 2.5 MB, and a channel change should only ever seek and read.
        foreach (var channel in channels.Where(c => !previous.ContainsKey(c.Directory)))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _ = channel.Index.Count;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Can't read the keyframe index of {Channel}", channel.Manifest.Name);
                }
            });
        }
        if (channels.Count != _channels.Count || channels.Where((c, i) => !ReferenceEquals(c, _channels[i])).Any())
        {
            logger.LogInformation(
                "Linear TV store: {Count} channels ({Channels})",
                channels.Count,
                string.Join(", ", channels.Select(c => $"{c.Manifest.Number} {c.Manifest.Name}")));
        }

        return _channels = channels;
    }
}
