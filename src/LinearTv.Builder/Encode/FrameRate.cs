using LinearTv.Builder.Library;

namespace LinearTv.Builder.Encode;

/// <summary>A frame rate as an exact fraction, e.g. 24000/1001 for "23.976".</summary>
internal sealed record FrameRate(int Numerator, int Denominator)
{
    public static readonly FrameRate Film = new(24000, 1001);
    public static readonly FrameRate Pal = new(25, 1);
    public static readonly FrameRate Ntsc = new(30000, 1001);

    private static readonly FrameRate[] Standard = [Film, new(24, 1), Pal, Ntsc, new(30, 1)];

    public double Value => (double)Numerator / Denominator;

    /// <summary>Frames in one 2-second keyframe interval, rounded: 48 at 23.976, 50 at 25.</summary>
    public int GopFrames => (int)Math.Round(Value * 2);

    /// <summary>
    /// The standard rate a source's rate belongs to. 50 and 60 fps sources count as 25 and 30: a
    /// channel never runs above 30 fps. Unknown or odd rates count as 25.
    /// </summary>
    public static FrameRate Nearest(double fps)
    {
        if (fps > 31)
        {
            fps /= 2;
        }

        var best = Standard.MinBy(r => Math.Abs(r.Value - fps))!;
        return Math.Abs(best.Value - fps) / best.Value < 0.02 ? best : Pal;
    }

    /// <summary>
    /// A channel's frame rate: the one most of its running time is already in, so the fewest
    /// minutes are converted. Ties go to the lower rate.
    /// </summary>
    public static FrameRate ForChannel(IEnumerable<SourceItem> items)
        => items
            .Where(i => i.Video is not null)
            .GroupBy(i => Nearest(i.Video!.FrameRate))
            .Select(g => (Rate: g.Key, Ticks: g.Sum(i => i.RuntimeTicks)))
            .OrderByDescending(g => g.Ticks)
            .ThenBy(g => g.Rate.Value)
            .Select(g => g.Rate)
            .FirstOrDefault() ?? Pal;

    public override string ToString() => Denominator == 1 ? $"{Numerator}" : $"{Numerator}/{Denominator}";

    /// <summary>For people: "23.976", "25".</summary>
    public string Display => Denominator == 1 ? $"{Numerator}" : $"{Value:0.###}";
}
