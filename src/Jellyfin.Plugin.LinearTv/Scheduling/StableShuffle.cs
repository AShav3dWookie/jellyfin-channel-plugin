namespace Jellyfin.Plugin.LinearTv.Scheduling;

/// <summary>
/// A shuffle that gives the same order for the same seed on every run, machine and .NET version.
/// </summary>
/// <remarks>
/// Deliberately avoids <see cref="string.GetHashCode()"/> (randomised per process, so every
/// restart would reshuffle every channel) and <see cref="Random"/> (its seeded sequence is not
/// a documented guarantee). FNV-1a and SplitMix64 are fixed, published algorithms.
/// </remarks>
internal static class StableShuffle
{
    /// <summary>Seed for one cycle of one channel.</summary>
    public static ulong Seed(string channelId, long cycle)
        => Fnv1a64(channelId) ^ unchecked((ulong)cycle * 0x9E3779B97F4A7C15UL);

    /// <summary>Fisher–Yates over a copy of <paramref name="items"/>.</summary>
    public static T[] Shuffle<T>(IReadOnlyList<T> items, ulong seed)
    {
        var result = items.ToArray();
        var state = seed;
        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = (int)(SplitMix64(ref state) % (ulong)(i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    /// <summary>64-bit FNV-1a over the string's UTF-16 code units.</summary>
    public static ulong Fnv1a64(string value)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var c in value)
        {
            hash ^= c;
            hash = unchecked(hash * 0x100000001B3UL);
        }

        return hash;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        var z = unchecked(state += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }
}
