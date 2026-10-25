using LinearTv.Builder.Encode;
using LinearTv.Builder.Import;
using LinearTv.Builder.Library;

namespace LinearTv.Builder;

/// <summary>Human-readable output for check and build.</summary>
internal static class Report
{
    /// <summary>How each channel's content resolved, in the layout 0.3's <c>./x channels check</c> used.</summary>
    public static void Print(ImportReport report, TextWriter output)
    {
        output.WriteLine(report.Ok ? "OK: everything resolved" : "REFUSED: fix the X lines");
        foreach (var error in report.Errors)
        {
            output.WriteLine($"  ERROR {error}");
        }

        foreach (var channel in report.Channels)
        {
            var how = new List<string>();
            if (channel.Shuffle)
            {
                how.Add("shuffle");
            }

            if (channel.Passes > 1)
            {
                how.Add($"{channel.Passes} passes");
            }

            output.WriteLine();
            output.WriteLine($"{channel.Number} {channel.Name}{(how.Count > 0 ? $" ({string.Join(", ", how)})" : string.Empty)}");
            foreach (var content in channel.Content)
            {
                output.WriteLine(content switch
                {
                    { Matched: null } => $"  X  {content.Requested}: {content.Note}",
                    { Note: not null } => $"  ~  {content.Requested} -> {content.Matched}   ({content.Note})",
                    _ => $"  ok {content.Requested} -> {content.Matched}",
                });
            }

            foreach (var problem in channel.Problems.Where(p => p.StartsWith("Missing", StringComparison.Ordinal)
                || p.StartsWith("No content", StringComparison.Ordinal) || p.StartsWith("passes", StringComparison.Ordinal)))
            {
                output.WriteLine($"  X  {problem}");
            }

            foreach (var warning in channel.Warnings)
            {
                output.WriteLine($"  !  {warning}");
            }
        }

        output.WriteLine();
        output.WriteLine("Legend: ok exact match, ~ matched with a note (check it), X refused, ! warning");
    }

    /// <summary>What a channel will be: its items, length, format, and what still needs encoding.</summary>
    public static void Print(ChannelPlan plan, PieceEncoder? encoder, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"==> {plan.Channel.Number} {plan.Channel.Name}");
        var once = TimeSpan.FromTicks(plan.Items.Sum(i => i.Item.RuntimeTicks));
        output.WriteLine(
            $"    {plan.Items.Count} item{(plan.Items.Count == 1 ? string.Empty : "s")}, {once.TotalHours:0.#} h"
            + (plan.Channel.Passes > 1 ? $" x {plan.Channel.Passes} passes = {plan.Length.TotalHours:0.#} h" : string.Empty)
            + $", loops every {Describe(plan.Length)}");
        output.WriteLine(
            $"    {EncodeProfile.Width}x{EncodeProfile.Height} at {plan.FrameRate.Display} fps; "
            + $"{plan.Items.Count(i => Math.Abs(FrameRate.Nearest(i.Item.Video!.FrameRate).Value - plan.FrameRate.Value) > 0.01)} item(s) converted from another rate; "
            + $"{plan.Items.Count(i => i.Item.Video!.Hdr)} HDR (tone-mapped); {plan.Items.Count(i => i.Item.Video!.Interlaced)} interlaced");
        output.WriteLine($"    {(plan.Chunked ? "hour-long files" : "one file")}");
        var bySource = plan.Items.GroupBy(i => i.Origin.Language is null ? "unknown" : i.Origin.Source.StartsWith("TMDb", StringComparison.Ordinal) ? "TMDb" : "production country")
            .Select(g => $"{g.Count()} from {g.Key}");
        output.WriteLine($"    original language: {string.Join(", ", bySource)}");
        var full = plan.Items.Count(i => i.Choice.Subtitle is { } s && !TrackSelector.IsForced(s));
        var forced = plan.Items.Count(i => i.Choice.Subtitle is { } s && TrackSelector.IsForced(s));
        output.WriteLine($"    subtitles burned in: {full} full, {forced} forced; {plan.Items.Count(i => i.Choice.Audio is null)} with no audio");
        foreach (var item in plan.Items.Where(i => i.Choice.Subtitle is not null || (i.Choice.Audio?.Language is { } l && l != plan.Channel.Language)))
        {
            output.WriteLine(
                $"  *  {item.Item}: {Languages.Name(item.Choice.Audio?.Language ?? item.Origin.Language)} audio"
                + (item.Choice.Subtitle is { } sub ? $", {(TrackSelector.IsForced(sub) ? "forced" : "full")} {Languages.Name(sub.Language)} subtitles ({sub.Codec}{(sub.ExternalPath is null ? string.Empty : ", external")})" : string.Empty));
        }

        if (encoder is not null)
        {
            var missing = plan.Items.Where(i => !encoder.IsEncoded(i, plan.FrameRate)).ToList();
            output.WriteLine(missing.Count == 0
                ? "    all items already encoded (left from an earlier run)"
                : $"    to encode: {missing.Count} items, {TimeSpan.FromTicks(missing.Sum(i => i.Item.RuntimeTicks)).TotalHours:0.#} h, at preset {encoder.Preset}");
        }

        foreach (var skipped in plan.Skipped)
        {
            output.WriteLine($"  -  left out: {skipped}");
        }

        foreach (var warning in plan.Warnings)
        {
            output.WriteLine($"  !  {warning}");
        }
    }

    private static string Describe(TimeSpan span) => span.TotalDays >= 2
        ? $"{span.TotalDays:0.#} days"
        : span.TotalHours >= 2 ? $"{span.TotalHours:0.#} hours" : $"{span.TotalMinutes:0} minutes";
}
