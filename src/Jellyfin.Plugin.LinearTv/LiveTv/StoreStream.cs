using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinearTv.LiveTv;

/// <summary>
/// A built channel as one endless MPEG-TS byte stream: from a keyframe part-way through the
/// channel's files, to the end of the last file, then round again from the start, for as long as
/// someone reads.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is decoded or re-encoded: the bytes are the channel files' own. The PAT and PMT go
/// first, so a player joining mid-file knows the stream's layout before the first keyframe.
/// </para>
/// <para>
/// Within the files, timestamps already run continuously from programme to programme (the builder
/// made them so). The one join is where the loop goes round: <see cref="TsSplicer"/> restamps the
/// start of the channel to follow on from its end. <see cref="TsPacer"/> sends a burst first, so
/// a browser has its first segments at once, then real time.
/// </para>
/// </remarks>
internal sealed class StoreStream : Stream
{
    /// <summary>
    /// Stream time sent at once on tuning, before pacing to real time. Covers the three HLS
    /// segments Jellyfin waits for before a browser can start, with a margin.
    /// </summary>
    public static readonly TimeSpan InitialBurst = TimeSpan.FromSeconds(35);

    private readonly IReadOnlyList<(string Path, long Bytes)> _files;
    private readonly byte[] _psi;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly TimeProvider _time;
    private readonly TsSplicer _splicer = new();
    private readonly TsPacer _pacer;
    private readonly byte[] _chunk = new byte[64 * 1024];

    private int _fileIndex;
    private long _fileOffset;
    private FileStream? _file;
    private bool _started;

    /// <param name="files">The channel's stream files in order, each with its length.</param>
    /// <param name="startOffset">Where to start, across the files end to end: a keyframe's offset.</param>
    /// <param name="psi">The channel's PAT and PMT packets.</param>
    /// <param name="name">The channel's name, for logs.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="time">The clock pacing is measured by.</param>
    public StoreStream(IReadOnlyList<(string Path, long Bytes)> files, long startOffset, byte[] psi, string name, ILogger logger, TimeProvider? time = null)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("A channel has at least one file.", nameof(files));
        }

        _files = files;
        _psi = psi;
        _name = name;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _pacer = new TsPacer(_time, InitialBurst);

        // The offset is across all files: find the file it falls in.
        while (_fileIndex < files.Count - 1 && startOffset >= files[_fileIndex].Bytes)
        {
            startOffset -= files[_fileIndex].Bytes;
            _fileIndex++;
        }

        _fileOffset = Math.Clamp(startOffset, 0, files[_fileIndex].Bytes);
    }

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
        while (true)
        {
            var ready = await SendPacedAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (ready > 0)
            {
                return ready;
            }

            if (!_started)
            {
                _started = true;
                _splicer.BeginProgramme();
                _splicer.Write(_psi);
            }

            _file ??= Open();
            var read = await _file.ReadAsync(_chunk, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _splicer.Write(_chunk.AsSpan(0, read));
                continue;
            }

            _file.Dispose();
            _file = null;
            _fileOffset = 0;
            _fileIndex++;
            if (_fileIndex == _files.Count)
            {
                // The end of the channel: round again from the start, restamped to follow on.
                _logger.LogDebug("{Channel}: looping back to the start", _name);
                _fileIndex = 0;
                _splicer.EndProgramme();
                _splicer.BeginProgramme();
            }
        }
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
            _file?.Dispose();
            _file = null;
        }

        base.Dispose(disposing);
    }

    private FileStream Open()
    {
        var (path, _) = _files[_fileIndex];
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(_fileOffset, SeekOrigin.Begin);
        return file;
    }

    /// <summary>
    /// Copies out spliced packets as their time comes. Hands over what is already due rather than
    /// holding it, and waits only when nothing is due yet. Returns 0 once the spliced output is
    /// exhausted, so the caller reads more of the file.
    /// </summary>
    private async ValueTask<int> SendPacedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            if (!_splicer.HasPartialPacket)
            {
                var next = _splicer.PeekPacket();
                if (next is null)
                {
                    break;
                }

                var wait = _pacer.DelayFor(next);
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
}
