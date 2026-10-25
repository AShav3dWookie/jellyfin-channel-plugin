namespace LinearTv.Builder;

/// <summary>
/// The order a channel's items go into its file: the lineup once per pass, either as listed or
/// shuffled differently each pass. No item ever plays twice in a row, including where the file
/// loops back to its start.
/// </summary>
internal static class Lineup
{
    /// <param name="items">The channel's items, each once, in listed order.</param>
    /// <param name="shuffle">Shuffle each pass (stably: the same channel builds the same order).</param>
    /// <param name="passes">How many times the lineup is in the file.</param>
    /// <param name="seedKey">Seeds the shuffle; the channel number, so each channel differs.</param>
    public static List<T> Build<T>(IReadOnlyList<T> items, bool shuffle, int passes, string seedKey)
        where T : notnull
    {
        var result = new List<T>(items.Count * passes);

        // Two items can only alternate without repeating, which is what in-order already does.
        // Three or more also leaves room for the swaps below.
        if (!shuffle || items.Count < 3)
        {
            for (var p = 0; p < passes; p++)
            {
                result.AddRange(items);
            }

            return result;
        }

        for (var p = 0; p < passes; p++)
        {
            var order = StableShuffle.Shuffle(items, StableShuffle.Seed(seedKey, p));
            if (p > 0 && EqualityComparer<T>.Default.Equals(order[0], result[^1]))
            {
                (order[0], order[1]) = (order[1], order[0]);
            }

            result.AddRange(order);
        }

        // The loop: the file's last item is followed by its first.
        if (passes > 1 && EqualityComparer<T>.Default.Equals(result[^1], result[0]))
        {
            (result[^1], result[^2]) = (result[^2], result[^1]);
        }

        return result;
    }
}
