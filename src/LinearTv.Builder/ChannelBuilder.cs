using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.LinearTv.Store;
using LinearTv.Builder.Assemble;
using LinearTv.Builder.Encode;
using LinearTv.Builder.Library;

namespace LinearTv.Builder;

/// <summary>
/// Builds one channel from its plan: encodes its items into pieces, fetches artwork, joins the
/// pieces, and writes a complete build folder. The folder is written under a <c>.partial</c> name
/// and renamed at the end, so an interrupted build never looks finished.
/// </summary>
/// <remarks>
/// Pieces are temporary. Each is deleted once it has been joined for the last time, so disk use
/// peaks at about the channel plus one pass of pieces. If the build stops before joining, the
/// pieces already encoded stay in the work folder and the next run reuses them.
/// </remarks>
internal sealed class ChannelBuilder(JellyfinClient client, PieceEncoder encoder, string workDir, string outDir, int jobs, TextWriter log)
{
    /// <summary>The build folder's name: channel number and build time, so builds sort by time.</summary>
    public static string FolderName(string number, DateTime builtUtc)
        => $"{Safe(number)}-{builtUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    /// <returns>The finished build folder, or null if any item failed to encode.</returns>
    public async Task<string?> BuildAsync(ChannelPlan plan, CancellationToken ct)
    {
        var rate = plan.FrameRate;
        var work = plan.Items.Where(i => !encoder.IsEncoded(i, rate)).ToList();
        if (work.Count > 0)
        {
            log.WriteLine(
                $"  encoding {work.Count} of {plan.Items.Count} items ({TimeSpan.FromTicks(work.Sum(w => w.Item.RuntimeTicks)).TotalHours:0.#} h), {jobs} at a time"
                + (work.Count < plan.Items.Count ? "; the rest are left from an earlier run" : string.Empty));
            var failures = await encoder.EncodeAsync(work, rate, jobs, ct).ConfigureAwait(false);
            if (failures.Count > 0)
            {
                log.WriteLine(
                    $"  {plan.Channel.Number} not built: {failures.Count} item(s) failed to encode. "
                    + "Fix or remove them and build again; the items that did encode are kept for the next run.");
                return null;
            }
        }

        var built = DateTime.UtcNow;
        var final = Path.Combine(outDir, FolderName(plan.Channel.Number, built));
        var partial = final + ".partial";
        Directory.CreateDirectory(Path.Combine(partial, "art"));

        var images = await FetchArtworkAsync(plan, partial, ct).ConfigureAwait(false);
        var paths = plan.Programmes.Select(p => encoder.PathFor(p, rate)).ToList();
        var lastUse = paths.Select((path, i) => (path, i)).GroupBy(x => x.path).ToDictionary(g => g.Key, g => g.Max(x => x.i));
        var pieces = plan.Programmes
            .Select((p, i) => new AssemblyPiece(paths[i], ChannelPlan.GuideEntry(p.Item, images.GetValueOrDefault(p.Item.Id)), DeleteAfter: lastUse[paths[i]] == i))
            .ToList();

        log.WriteLine($"  joining {pieces.Count} programmes into {(plan.Chunked ? "hour-long files" : "one file")}");
        var result = ChannelAssembler.Assemble(pieces, partial, plan.Chunked, rate.Value, log);

        var manifest = new ChannelManifest
        {
            Number = plan.Channel.Number,
            Name = plan.Channel.Name,
            Built = built,
            CycleTicks = result.CycleTicks,
            Video = EncodeProfile.Video(rate),
            Audio = EncodeProfile.Audio(),
            Psi = result.Psi,
            Files = result.Files,
            Programmes = result.Programmes,
        };

        // Written last: a folder with a manifest is a complete build.
        await File.WriteAllTextAsync(
            Path.Combine(partial, ChannelManifest.FileName), JsonSerializer.Serialize(manifest, ChannelManifest.JsonOptions), ct).ConfigureAwait(false);
        Directory.Move(partial, final);

        var bytes = result.Files.Sum(f => f.Bytes);
        log.WriteLine(
            $"  built {final}: {TimeSpan.FromTicks(result.CycleTicks).TotalHours:0.##} h, {result.Programmes.Count} programmes, "
            + $"{result.Files.Count} file(s), {bytes / 1e9:0.0} GB, {result.Keyframes} keyframes");
        RemoveOlderBuilds(plan.Channel.Number, final);
        return final;
    }

    /// <summary>
    /// Deletes this channel's earlier local builds (and abandoned partial ones): each is a full
    /// copy of the channel, superseded by the one just finished.
    /// </summary>
    private void RemoveOlderBuilds(string number, string keep)
    {
        var prefix = Safe(number) + "-";
        foreach (var dir in Directory.EnumerateDirectories(outDir, prefix + "*"))
        {
            var name = Path.GetFileName(dir);
            var stamp = name[prefix.Length..].Replace(".partial", string.Empty, StringComparison.Ordinal);
            if (Path.GetFullPath(dir) == Path.GetFullPath(keep)
                || !DateTime.TryParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                continue;
            }

            Directory.Delete(dir, recursive: true);
            log.WriteLine($"  removed older build {name}");
        }
    }

    private async Task<Dictionary<Guid, string>> FetchArtworkAsync(ChannelPlan plan, string buildDir, CancellationToken ct)
    {
        var artCache = Path.Combine(workDir, "art");
        Directory.CreateDirectory(artCache);
        var result = new Dictionary<Guid, string>();
        foreach (var item in plan.Items.Select(i => i.Item).Where(i => i.ImageItemId is not null && i.ImageTag is not null))
        {
            var name = $"{item.ImageItemId}-{Safe(item.ImageTag!)}.jpg";
            var cached = Path.Combine(artCache, name);
            if (!File.Exists(cached))
            {
                var bytes = await client.GetImageAsync(item.ImageItemId!, item.ImageTag!, ct).ConfigureAwait(false);
                if (bytes is null)
                {
                    continue;
                }

                await File.WriteAllBytesAsync(cached, bytes, ct).ConfigureAwait(false);
            }

            var target = Path.Combine(buildDir, "art", name);
            if (!File.Exists(target))
            {
                File.Copy(cached, target);
            }

            result[item.Id] = $"art/{name}";
        }

        return result;
    }

    private static string Safe(string text)
        => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_'));
}
