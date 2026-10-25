using System.Diagnostics;
using System.Globalization;

namespace LinearTv.Builder.Encode;

/// <summary>
/// Encodes items into pieces in the work folder: one ffmpeg run per item, source to a piece TS in
/// the channel format. A piece is written under a temporary name and renamed when ffmpeg
/// succeeds, so the work folder never holds a half-encoded piece, and a build that stops
/// part-way can pick up where it left off.
/// </summary>
internal sealed class PieceEncoder(string ffmpeg, string workDir, string preset, bool hardwareDecode, TextWriter log)
{
    public string PathFor(PlannedItem item, FrameRate rate, Resolution size)
        => Path.Combine(workDir, EncodeProfile.CacheKey(item.Item, item.Choice, rate, preset, size) + ".ts");

    public string Preset => preset;

    public bool IsEncoded(PlannedItem item, FrameRate rate, Resolution size) => File.Exists(PathFor(item, rate, size));

    /// <summary>Encodes every item given, <paramref name="jobs"/> at a time.</summary>
    /// <returns>The items that failed, with why.</returns>
    public async Task<List<(PlannedItem Item, string Error)>> EncodeAsync(
        IReadOnlyList<PlannedItem> work, FrameRate rate, Resolution size, int jobs, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var failures = new List<(PlannedItem, string)>();
        var done = 0;
        var started = Stopwatch.StartNew();
        var totalTicks = work.Sum(w => w.Item.RuntimeTicks);
        long doneTicks = 0;

        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, async (w, token) =>
        {
            var clock = Stopwatch.StartNew();
            var error = await EncodeOneAsync(w, rate, size, token).ConfigureAwait(false);
            var speed = TimeSpan.FromTicks(w.Item.RuntimeTicks) / clock.Elapsed;
            lock (failures)
            {
                done++;
                doneTicks += w.Item.RuntimeTicks;
                if (error is null)
                {
                    var overall = TimeSpan.FromTicks(doneTicks) / started.Elapsed;
                    var left = overall > 0 ? TimeSpan.FromTicks((long)((totalTicks - doneTicks) / overall)) : TimeSpan.Zero;
                    log.WriteLine(
                        $"    [{done}/{work.Count}] {w.Item}  {TimeSpan.FromTicks(w.Item.RuntimeTicks):h\\:mm\\:ss} in {clock.Elapsed:h\\:mm\\:ss} ({speed:0.0}x)"
                        + (done < work.Count ? $"  ~{left:h\\:mm} left" : string.Empty));
                }
                else
                {
                    failures.Add((w, error));
                    log.WriteLine($"    [{done}/{work.Count}] FAILED {w.Item}: {error}");
                }
            }
        }).ConfigureAwait(false);

        return failures;
    }

    private static string Quote(string arg) => arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '[') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

    private async Task<string?> EncodeOneAsync(PlannedItem item, FrameRate rate, Resolution size, CancellationToken ct)
    {
        var error = await EncodeOneAsync(item, rate, size, gpuFrames: true, ct).ConfigureAwait(false);
        if (error is not null && hardwareDecode && EncodeProfile.StaysOnGpu(item.Item.Video!, item.Choice))
        {
            // The GPU path is an optimisation: an odd stream it can't take still encodes on the CPU.
            log.WriteLine($"    {item.Item}: GPU scaling failed ({error}); retrying on the CPU");
            error = await EncodeOneAsync(item, rate, size, gpuFrames: false, ct).ConfigureAwait(false);
        }

        return error;
    }

    private async Task<string?> EncodeOneAsync(PlannedItem item, FrameRate rate, Resolution size, bool gpuFrames, CancellationToken ct)
    {
        var final = PathFor(item, rate, size);
        var partial = final + ".partial";
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in EncodeProfile.Arguments(item.Item, item.Choice, item.LocalPath, item.LocalSubtitlePath, rate, partial, preset, hardwareDecode, gpuFrames, size))
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {ffmpeg}");
        try
        {
            // -progress reports on stdout; drained so ffmpeg never blocks on a full pipe.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var errors = (await stderr.ConfigureAwait(false)).Trim();

            var log = Path.ChangeExtension(final, ".log");
            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                File.Delete(partial);

                // The whole story for whoever looks into it: the exact command, and everything ffmpeg said.
                await File.WriteAllTextAsync(
                    log,
                    $"{item.Item}\n{item.LocalPath}\n\n{ffmpeg} {string.Join(' ', info.ArgumentList.Select(Quote))}\n\n{errors}\n",
                    CancellationToken.None).ConfigureAwait(false);
                var lastLine = errors.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim();
                return $"ffmpeg exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}{(lastLine is null ? string.Empty : $": {lastLine}")} (details: {log})";
            }

            File.Delete(log);
            File.Move(partial, final, overwrite: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(partial);
            throw;
        }
    }
}
