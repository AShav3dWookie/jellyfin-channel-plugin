using Jellyfin.Plugin.LinearTv.LiveTv;
using Jellyfin.Plugin.LinearTv.Store;

namespace LinearTv.Builder.Assemble;

/// <summary>One programme of a channel being assembled: its encoded piece, and its guide entry.</summary>
/// <param name="DeleteAfter">Delete the piece once joined: this is its last appearance in the channel.</param>
internal sealed record AssemblyPiece(string Path, ManifestProgramme Programme, bool DeleteAfter = false);

/// <summary>What assembly wrote, for the manifest.</summary>
internal sealed record AssemblyResult(
    List<ManifestFile> Files, List<ManifestProgramme> Programmes, long CycleTicks, string Psi, int Keyframes);

/// <summary>
/// Joins a channel's pieces into its stream file(s): the bytes of each piece in turn, through
/// <see cref="TsSplicer"/>, so timestamps and continuity counters run on unbroken from the first
/// programme to the last. Nothing is decoded or re-encoded. While writing, records where every
/// video keyframe and every programme starts.
/// </summary>
internal static class ChannelAssembler
{
    /// <summary>Channels at least this long are written as hour-long files rather than one.</summary>
    public static readonly TimeSpan ChunkThreshold = TimeSpan.FromHours(100);

    public static readonly TimeSpan ChunkLength = TimeSpan.FromHours(1);

    private const long Modulus = 1L << 33;
    private const long HalfModulus = 1L << 32;
    private const int H264StreamType = 0x1B;
    private const int HevcStreamType = 0x24;

    /// <param name="pieces">The programmes in channel order.</param>
    /// <param name="directory">The build folder to write into.</param>
    /// <param name="chunked">Write hour-long files instead of one.</param>
    /// <param name="frameRate">The channel's frame rate: the last frame's length ends the loop.</param>
    /// <param name="log">Progress, one line per tenth of the pieces.</param>
    /// <param name="chunkLength">How long each chunk is: <see cref="ChunkLength"/>, except in tests.</param>
    public static AssemblyResult Assemble(
        IReadOnlyList<AssemblyPiece> pieces, string directory, bool chunked, double frameRate, TextWriter? log = null, TimeSpan? chunkLength = null)
    {
        if (pieces.Count == 0)
        {
            throw new ArgumentException("A channel needs at least one programme.", nameof(pieces));
        }

        var splicer = new TsSplicer();
        using var output = new ChunkWriter(directory, chunked);
        var index = new List<(long Ticks, long Offset)>();
        var programmes = new List<ManifestProgramme>();
        var input = new byte[1 << 20];
        var drained = new byte[TsPacket.Size * 4096];
        var chunk90k = ToNinetyK((chunkLength ?? ChunkLength).Ticks);

        byte[]? pat = null, pmt = null;
        int? pmtPid = null;
        List<(int StreamType, int Pid)>? layout = null;
        var videoPid = -1;
        long? lastRaw = null;
        long position90k = 0;      // video decode time since the file's first frame, unwrapped
        long lastVideo90k = 0;
        var nextChunk90k = chunk90k;
        ManifestProgramme? starting = null;
        var checkLayout = false;
        string? piecePath = null;

        for (var n = 0; n < pieces.Count; n++)
        {
            var piece = pieces[n];
            piecePath = piece.Path;
            splicer.BeginProgramme();
            starting = Copy(piece.Programme);
            checkLayout = true;

            using (var stream = new FileStream(piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                int read;
                while ((read = stream.Read(input)) > 0)
                {
                    splicer.Write(input.AsSpan(0, read));
                    Drain();
                }
            }

            splicer.EndProgramme();
            Drain();
            if (starting is not null)
            {
                throw new InvalidDataException($"{piece.Path} has no video.");
            }

            if (piece.DeleteAfter)
            {
                File.Delete(piece.Path);
            }

            if (log is not null && pieces.Count >= 10 && (n + 1) % (pieces.Count / 10) == 0)
            {
                log.WriteLine($"    joined {n + 1}/{pieces.Count} programmes, {output.Position / 1e9:0.0} GB");
            }
        }

        if (pat is null || pmt is null)
        {
            throw new InvalidDataException("The pieces have no PAT/PMT: not ffmpeg MPEG-TS output.");
        }

        // The loop rejoins one frame after the last, plus the splicer's gap between programmes.
        var cycle90k = lastVideo90k + (long)Math.Round(90_000 / frameRate) + TsSplicer.ProgrammeGap;
        for (var i = 0; i < programmes.Count; i++)
        {
            var end = i + 1 < programmes.Count ? programmes[i + 1].Start : ToTicks(cycle90k);
            programmes[i].Duration = end - programmes[i].Start;
        }

        output.Dispose();
        using (var indexFile = new FileStream(Path.Combine(directory, "index.bin"), FileMode.Create))
        {
            KeyframeIndex.Write(indexFile, index);
        }

        return new AssemblyResult(output.Files, programmes, ToTicks(cycle90k), Convert.ToBase64String([.. pat, .. pmt]), index.Count);

        void Drain()
        {
            int count;
            while ((count = splicer.Read(drained)) > 0)
            {
                for (var i = 0; i + TsPacket.Size <= count; i += TsPacket.Size)
                {
                    Handle(drained.AsSpan(i, TsPacket.Size));
                }
            }
        }

        void Handle(ReadOnlySpan<byte> packet)
        {
            var pid = TsPacket.Pid(packet);
            if (pid == TsPacket.PatPid && pat is null)
            {
                pat = packet.ToArray();
                pmtPid = TsPacket.PmtPid(packet);
            }
            else if (pid == pmtPid && TsPacket.PayloadUnitStart(packet))
            {
                if (pmt is null)
                {
                    pmt = packet.ToArray();
                    layout = TsPacket.PmtStreams(packet);
                    videoPid = layout.Where(s => s.StreamType is H264StreamType or HevcStreamType).Select(s => s.Pid).DefaultIfEmpty(-1).First();
                    if (videoPid < 0)
                    {
                        throw new InvalidDataException($"{piecePath} has no H.264 or H.265 video stream.");
                    }
                }
                else if (checkLayout && !TsPacket.PmtStreams(packet).SequenceEqual(layout!))
                {
                    // Every piece comes from the same profile, so this means a stale or foreign cache file.
                    throw new InvalidDataException($"{piecePath} has a different stream layout from the channel's first programme.");
                }

                checkLayout = false;
            }

            if (pid == videoPid && TsPacket.TryDecodeTimestamp(packet, out var dts))
            {
                if (lastRaw is { } last)
                {
                    var step = Mod(dts - last);
                    position90k += step < HalfModulus ? step : step - Modulus;
                }

                lastRaw = dts;
                if (starting is not null)
                {
                    starting.Start = ToTicks(position90k);
                    programmes.Add(starting);
                    starting = null;
                }

                if (TsPacket.RandomAccess(packet))
                {
                    if (chunked && position90k >= nextChunk90k)
                    {
                        output.NextFile();
                        while (nextChunk90k <= position90k)
                        {
                            nextChunk90k += chunk90k;
                        }
                    }

                    index.Add((ToTicks(position90k), output.Position));
                }

                lastVideo90k = position90k;
            }

            output.Write(packet);
        }
    }

    /// <summary>90 kHz to 100 ns ticks.</summary>
    internal static long ToTicks(long ninetyK) => ninetyK * 1000 / 9;

    internal static long ToNinetyK(long ticks) => ticks * 9 / 1000;

    private static long Mod(long value) => ((value % Modulus) + Modulus) % Modulus;

    private static ManifestProgramme Copy(ManifestProgramme p) => new()
    {
        Title = p.Title, EpisodeTitle = p.EpisodeTitle, Season = p.Season, Episode = p.Episode, Year = p.Year,
        Movie = p.Movie, Overview = p.Overview, Genres = p.Genres, Rating = p.Rating, Image = p.Image,
    };

    /// <summary>Writes the channel's bytes across one file, or hour-long chunks.</summary>
    private sealed class ChunkWriter(string directory, bool chunked) : IDisposable
    {
        private FileStream? _file;

        public List<ManifestFile> Files { get; } = [];

        /// <summary>Gets the byte offset across all files written so far.</summary>
        public long Position { get; private set; }

        public void Write(ReadOnlySpan<byte> packet)
        {
            if (_file is null)
            {
                Open();
            }

            _file!.Write(packet);
            Position += packet.Length;
            Files[^1].Bytes += packet.Length;
        }

        public void NextFile()
        {
            if (_file is not null && Files[^1].Bytes > 0)
            {
                _file.Dispose();
                _file = null;
            }
        }

        public void Dispose()
        {
            _file?.Dispose();
            _file = null;
        }

        private void Open()
        {
            var name = chunked ? $"channel-{Files.Count:0000}.ts" : "channel.ts";
            _file = new FileStream(Path.Combine(directory, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            Files.Add(new ManifestFile { Name = name });
        }
    }
}
