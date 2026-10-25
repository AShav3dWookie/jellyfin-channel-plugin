using System.Globalization;
using System.Text.Json;
using LinearTv.Builder;
using LinearTv.Builder.Encode;
using LinearTv.Builder.Import;
using LinearTv.Builder.Library;

const string Usage = """
    usage: linear-tv-builder <check|build> <channel-file> [options]

      check     resolve the file against the server and show what each channel would be;
                nothing is encoded or written
      build     encode, then write a build folder per channel

    options:
      --server URL      Jellyfin server to read the library from (required)
      --key KEY         Jellyfin API key (or set LINEARTV_API_KEY)
      --map FROM=TO     server path prefix -> local path, e.g. /media=\\TRUENAS\media; repeatable
      --tmdb-key KEY    TMDb API key or read access token, for original languages (or set TMDB_API_KEY)
      --work DIR        work folder: pieces while a channel builds, TMDb answers, artwork (required)
      --out DIR         where build folders go (build; required)
      --ffmpeg PATH     ffmpeg executable (default: ffmpeg on PATH)
      --jobs N          encodes at a time (default 3)
      --preset pN       NVENC preset, p1 (fastest) to p7 (best quality per bit); default p4
      --only N[,N...]   just these channel numbers
      --no-hwdecode     decode on the CPU instead of the GPU
    """;

if (args.Length < 2 || args[0] is not ("check" or "build"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var file = args[1];
string? server = null, key = Environment.GetEnvironmentVariable("LINEARTV_API_KEY"), work = null, outDir = null, ffmpeg = "ffmpeg";
var tmdbKey = Environment.GetEnvironmentVariable("TMDB_API_KEY");
var maps = new List<string>();
var only = new HashSet<string>();
var jobs = 3;
var hardwareDecode = true;
var preset = EncodeProfile.DefaultPreset;
for (var i = 2; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--server": server = Next(); break;
        case "--key": key = Next(); break;
        case "--map": maps.Add(Next()); break;
        case "--tmdb-key": tmdbKey = Next(); break;
        case "--work": work = Next(); break;
        case "--out": outDir = Next(); break;
        case "--ffmpeg": ffmpeg = Next(); break;
        case "--jobs": jobs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--only": only.UnionWith(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
        case "--no-hwdecode": hardwareDecode = false; break;
        case "--preset": preset = Next().Trim().ToLowerInvariant(); break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}\n\n{Usage}");
            return 2;
    }
}

if (server is null || string.IsNullOrEmpty(key) || work is null || (command == "build" && outDir is null))
{
    Console.Error.WriteLine($"missing --server, --key, --work{(command == "build" ? " or --out" : string.Empty)}\n\n{Usage}");
    return 2;
}

if (!EncodeProfile.IsPreset(preset))
{
    Console.Error.WriteLine($"--preset must be p1 to p7, not '{preset}'");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("stopping: finished pieces are kept for the next run");
    cts.Cancel();
};

ChannelDocument document;
try
{
    document = JsonSerializer.Deserialize<ChannelDocument>(await File.ReadAllTextAsync(file), ChannelDocument.JsonOptions)
        ?? throw new JsonException("empty file");
}
catch (Exception ex) when (ex is JsonException or IOException)
{
    Console.Error.WriteLine($"error: can't read {file}: {ex.Message}");
    return 2;
}

if (document.Version != 1)
{
    Console.Error.WriteLine($"error: {file} is format version {document.Version}; this builder reads version 1");
    return 2;
}

using var client = new JellyfinClient(server, key);
using var tmdb = string.IsNullOrWhiteSpace(tmdbKey) ? null : new TmdbClient(tmdbKey, Path.Combine(work, "tmdb.json"));
var origins = new OriginResolver(client, tmdb);
var paths = new PathMap(maps);

try
{
    Console.WriteLine($"==> Resolving {file} against {client.Server}");
    if (tmdb is null)
    {
        Console.WriteLine("==> No TMDb key: original languages come from production countries, which is less exact");
    }

    var catalogue = await client.GetCatalogueAsync(cts.Token);
    var (report, channels) = ChannelImporter.Resolve(document, catalogue);
    Report.Print(report, Console.Out);
    if (!report.Ok)
    {
        return 1;
    }

    var selected = channels.Where(c => only.Count == 0 || only.Contains(c.Number)).ToList();
    if (only.Count > 0 && selected.Count != only.Count)
    {
        Console.Error.WriteLine($"error: no channel {string.Join(", ", only.Except(channels.Select(c => c.Number)))} in {file}");
        return 2;
    }

    var encoder = new PieceEncoder(ffmpeg, Path.Combine(work, "pieces"), preset, hardwareDecode, Console.Out);
    var plans = new List<ChannelPlan>();
    foreach (var channel in selected)
    {
        var plan = await ChannelPlan.CreateAsync(channel, catalogue, client, origins, paths, cts.Token);
        plans.Add(plan);
        Report.Print(plan, encoder, Console.Out);
    }

    if (plans.Any(p => p.Items.Count == 0))
    {
        Console.Error.WriteLine("error: a channel has nothing it can play");
        return 1;
    }

    if (command == "check")
    {
        return 0;
    }

    Directory.CreateDirectory(outDir!);

    // A build takes hours and Windows doesn't count it as activity: without this the PC sleeps
    // part-way (it did, at the default 30 minutes). Until this process exits; the display may sleep.
    KeepAwake.WhileRunning();

    var builder = new ChannelBuilder(client, encoder, work, outDir!, jobs, Console.Out);
    var failed = 0;
    foreach (var plan in plans)
    {
        Console.WriteLine($"==> Building {plan.Channel.Number} {plan.Channel.Name}");
        if (await builder.BuildAsync(plan, cts.Token) is null)
        {
            failed++;
        }
    }

    return failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
