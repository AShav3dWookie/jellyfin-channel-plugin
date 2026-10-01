using Jellyfin.Plugin.LinearTv.Scheduling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>What a <see cref="ChannelStream"/> needs to know about its channel.</summary>
internal interface IChannelPlanner
{
    /// <summary>Human-readable channel name, for logs.</summary>
    string ChannelName { get; }

    /// <summary>Where a viewer tuning in now should start, or null if the channel is empty.</summary>
    TuneTarget? First();

    /// <summary>The programme scheduled after <paramref name="previous"/>, or null if none.</summary>
    ScheduledProgram? After(ScheduledProgram previous);

    /// <summary>The URL of Jellyfin's remux of an item from an offset. Fresh session each call.</summary>
    Task<string> UrlForAsync(Guid itemId, long offsetTicks);

    /// <summary>Human-readable programme description, for logs.</summary>
    string Describe(Guid itemId);

    /// <summary>
    /// The item's format as a player sees it, video and audio codec (e.g. "h264/aac"), or null if
    /// unknown. Programmes in the same format can play straight through; a change can't.
    /// </summary>
    string? FormatOf(Guid itemId);
}

/// <summary>
/// A channel as one endless MPEG-TS byte stream: the current programme from the live offset, then
/// each following programme from its start, in schedule order.
/// </summary>
/// <remarks>
/// <para>
/// The plugin only joins Jellyfin's own remux output here, passing it through a
/// <see cref="TsSplicer"/> so the timeline runs continuously across programmes. Nothing is decoded
/// or re-encoded.
/// </para>
/// <para>
/// The stream ends cleanly at the first programme in a different format from the one tuned in
/// on. Players set up their decoders once, at the start: measured, a browser's picture froze at
/// the first codec change and a TV-style decoder decoded only the matching programmes
/// (docs/testing.md channels test H). Ending instead sends the player back to the menu, and
/// tuning in again sets everything up for the new programme.
/// </para>
/// </remarks>
internal sealed class ChannelStream(
    IChannelPlanner planner, HttpClient http, ILogger logger, bool spliceTimestamps = true, TimeProvider? time = null) : Stream
{
    /// <summary>
    /// Stream time sent immediately on tuning, before pacing to real time. Must cover the three
    /// HLS segments Jellyfin waits for before a browser can start. Segments can only be cut at
    /// keyframes, and 10 s between keyframes is common (x264's default at 25 fps), so 3 × 10 s
    /// plus a margin.
    /// </summary>
    public static readonly TimeSpan InitialBurst = TimeSpan.FromSeconds(35);

    // Consecutive programmes that fail to open before the channel gives up, so a broken library
    // item is skipped but a broken server doesn't spin forever.
    private const int MaxConsecutiveFailures = 5;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TsSplicer? _splicer = spliceTimestamps ? new TsSplicer() : null;
    private readonly TsPacer? _pacer = spliceTimestamps ? new TsPacer(time ?? TimeProvider.System, InitialBurst) : null;
    private readonly byte[] _chunk = new byte[64 * 1024];

    private ScheduledProgram? _programme;
    private string? _tunedFormat;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private bool _started;
    private bool _finished;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_splicer is null)
        {
            return await ReadRawAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            var ready = await SendPacedAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (ready > 0 || _finished)
            {
                return ready;
            }

            if (_body is null)
            {
                if (!await OpenNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    _finished = true;
                    _splicer.EndProgramme(); // release anything still held back
                    continue;
                }

                _splicer.BeginProgramme();
            }

            var read = await _body!.ReadAsync(_chunk, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _splicer.Write(_chunk.AsSpan(0, read));
                continue;
            }

            // This programme's remux has ended: move straight on to the next one.
            _splicer.EndProgramme();
            CloseCurrent();
        }
    }

    /// <summary>
    /// Copies out spliced packets as their time comes. Hands over what is already due rather than
    /// holding it, and waits only when nothing is due yet. Returns 0 once the spliced output is
    /// exhausted, so the caller fetches more input.
    /// </summary>
    private async ValueTask<int> SendPacedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            if (!_splicer!.HasPartialPacket)
            {
                var next = _splicer.PeekPacket();
                if (next is null)
                {
                    break;
                }

                var wait = _pacer!.DelayFor(next);
                if (wait > TimeSpan.Zero)
                {
                    if (written > 0)
                    {
                        break;
                    }

                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _pacer.Sent(next);
            }

            written += _splicer.ReadPacket(buffer.Span[written..]);
        }

        return written;
    }

    private async ValueTask<int> ReadRawAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!_finished)
        {
            if (_body is null && !await OpenNextAsync(cancellationToken).ConfigureAwait(false))
            {
                _finished = true;
                break;
            }

            var read = await _body!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                return read;
            }

            CloseCurrent();
        }

        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseCurrent();
            _finished = true;
        }

        base.Dispose(disposing);
    }

    private async Task<bool> OpenNextAsync(CancellationToken cancellationToken)
    {
        for (var failures = 0; failures < MaxConsecutiveFailures; failures++)
        {
            ScheduledProgram? programme;
            long offset;
            if (!_started)
            {
                _started = true;
                var first = planner.First();
                programme = first?.Program;
                offset = first?.OffsetTicks ?? 0;
            }
            else
            {
                programme = _programme is null ? null : planner.After(_programme);
                offset = 0;
            }

            if (programme is null)
            {
                logger.LogInformation("{Channel}: nothing left to play", planner.ChannelName);
                return false;
            }

            // Compared with the programme the player was set up for. Unknown formats never stop
            // the channel: missing information shouldn't cut it short.
            var format = planner.FormatOf(programme.ItemId);
            if (_tunedFormat is not null && format is not null && format != _tunedFormat)
            {
                logger.LogInformation(
                    "{Channel}: {Programme} is {Format}, not {TunedFormat}; ending the stream so the player can tune in afresh",
                    planner.ChannelName, planner.Describe(programme.ItemId), format, _tunedFormat);
                return false;
            }

            _programme = programme;
            try
            {
                var url = await planner.UrlForAsync(programme.ItemId, offset).ConfigureAwait(false);
                _response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _response.EnsureSuccessStatusCode();
                _body = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "{Channel}: playing {Programme} from {Offset:hh\\:mm\\:ss}",
                    planner.ChannelName, planner.Describe(programme.ItemId), TimeSpan.FromTicks(offset));
                _tunedFormat ??= format;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never log the URL: it carries the API key.
                logger.LogWarning(
                    ex, "{Channel}: could not open {Programme}; skipping to the next programme",
                    planner.ChannelName, planner.Describe(programme.ItemId));
                CloseCurrent();
            }
        }

        logger.LogError("{Channel}: {Count} programmes in a row failed to open; ending the stream", planner.ChannelName, MaxConsecutiveFailures);
        return false;
    }

    private void CloseCurrent()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
    }
}
