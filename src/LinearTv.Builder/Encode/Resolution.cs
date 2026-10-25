namespace LinearTv.Builder.Encode;

/// <summary>
/// A channel's picture: its size, the bitrate ceiling that goes with it, and the constant quality
/// it's encoded at. One per channel: every programme is fitted inside it, so the stream never
/// changes size.
/// </summary>
/// <remarks>
/// 720p was the first choice, and it's fine for animation. For live action, measured on Die Hard
/// against the 1080p Blu-ray (VMAF): 720p CQ30 88.0 at 0.37 GB/h, 1080p CQ30 95.1 at 0.89 GB/h,
/// and 720p can't catch up with bits (CQ20 reached 91.5 at 1.9 GB/h). 1080p encodes at about half
/// the speed: 15x combined on the A5000 against 32x.
/// </remarks>
internal sealed record Resolution(string Name, int Width, int Height, string MaxRate, string BufferSize)
{
    public const int DefaultCq = 30;

    /// <summary>Gets NVENC's constant quality: lower is better and bigger. 30 unless the channel asks.</summary>
    public int Cq { get; init; } = DefaultCq;

    public static readonly Resolution Hd720 = new("720p", 1280, 720, "10M", "20M");

    // 20 Mb/s is H.265 Main tier level 4.1's own ceiling.
    public static readonly Resolution Hd1080 = new("1080p", 1920, 1080, "20M", "40M");

    public static Resolution Default => Hd720;

    /// <summary>The resolution a channel file names (<c>720p</c>, <c>1080p</c>), or null if it isn't one.</summary>
    public static Resolution? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "720p" or "720" => Hd720,
        "1080p" or "1080" => Hd1080,
        _ => null,
    };

    public override string ToString() => Cq == DefaultCq ? Name : $"{Name} CQ{Cq}";
}
