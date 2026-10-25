namespace LinearTv.Builder.Library;

/// <summary>Which subtitles to burn in.</summary>
internal enum SubtitleMode
{
    /// <summary>Full subtitles if the audio isn't in the viewer's language, else forced ones if any.</summary>
    Auto,

    None,

    /// <summary>Forced subtitles in the viewer's language, if any; never full ones.</summary>
    Forced,

    /// <summary>Full subtitles in the viewer's language, whatever the audio.</summary>
    Full,
}

/// <summary>What a channel file asks for, for one entry: the audio language to prefer, and subtitles.</summary>
/// <param name="Audio">Canonical language to choose the audio track by (allows a dub); null for the original language.</param>
internal sealed record TrackOptions(string? Audio = null, SubtitleMode Subtitles = SubtitleMode.Auto)
{
    public static readonly TrackOptions Default = new();
}

/// <summary>The tracks to encode for an item, or why it can't be in the channel.</summary>
/// <param name="Audio">Null when the item has no usable audio: silence is encoded.</param>
/// <param name="Subtitle">Null when nothing is burned in.</param>
/// <param name="Skip">Set when the item must be left out.</param>
internal sealed record TrackChoice(MediaTrack? Audio, MediaTrack? Subtitle, string? Skip = null)
{
    public static TrackChoice LeaveOut(string reason) => new(null, null, reason);
}

/// <summary>
/// Chooses an item's audio track and burned-in subtitles. Pure, so the rules are tested directly.
/// docs/architecture.md, "Audio track and subtitles", is the specification.
/// </summary>
internal static class TrackSelector
{
    private static readonly string[] NotProgrammeAudio = ["commentary", "description", "descriptive", "described"];

    /// <param name="item">The item, with all its tracks.</param>
    /// <param name="original">The title's original language, canonical; null if not known.</param>
    /// <param name="options">What the channel file asks for.</param>
    /// <param name="viewer">The viewer's language, canonical.</param>
    public static TrackChoice Choose(SourceItem item, string? original, TrackOptions options, string viewer)
    {
        var candidates = item.AudioTracks.Where(t => !IsNotProgrammeAudio(t)).ToList();
        MediaTrack? audio;
        if (candidates.Count == 0)
        {
            audio = null;
        }
        else if (options.Audio is { } wanted)
        {
            audio = Best(candidates.Where(t => t.Language == wanted));
            if (audio is null)
            {
                return TrackChoice.LeaveOut($"no {Languages.Name(wanted)} audio (has {Describe(candidates)})");
            }
        }
        else if (candidates.FirstOrDefault(t => t.IsOriginal) is { } flagged)
        {
            audio = flagged;
        }
        else if (original is not null)
        {
            // Untagged tracks are taken to be the original: a file with one unlabelled track is the norm.
            audio = Best(candidates.Where(t => t.Language == original)) ?? candidates.FirstOrDefault(t => t.Language is null);
            if (audio is null)
            {
                return TrackChoice.LeaveOut(
                    $"no {Languages.Name(original)} (original) audio, only {Describe(candidates)}; set \"audio\" to allow a dub");
            }
        }
        else
        {
            audio = candidates[0];
        }

        // What the viewer hears: the track's own tag, else (untagged) the original language.
        var heard = audio?.Language ?? (audio is null ? null : original);
        var inViewerLanguage = item.SubtitleTracks.Where(t => t.Language == viewer).ToList();
        var forced = inViewerLanguage.Where(IsForced).ToList();
        var full = inViewerLanguage.Where(t => !IsForced(t)).OrderBy(IsSdh).ToList();

        var mode = options.Subtitles;
        if (mode == SubtitleMode.Auto)
        {
            mode = heard is not null && heard != viewer ? SubtitleMode.Full : SubtitleMode.Forced;
        }

        switch (mode)
        {
            case SubtitleMode.Full when full.Count == 0:
                return TrackChoice.LeaveOut(heard is not null && heard != viewer && options.Subtitles == SubtitleMode.Auto
                    ? $"{Languages.Name(heard)} audio with no {Languages.Name(viewer)} subtitles"
                    : $"no full {Languages.Name(viewer)} subtitles");
            case SubtitleMode.Full:
                return new TrackChoice(audio, full[0]);
            case SubtitleMode.Forced:
                return new TrackChoice(audio, forced.FirstOrDefault());
            default:
                return new TrackChoice(audio, null);
        }
    }

    /// <summary>Flagged forced, or titled so: many files carry a "Forced" track without the flag.</summary>
    internal static bool IsForced(MediaTrack track)
        => track.IsForced || (track.Title?.Contains("forced", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsSdh(MediaTrack track)
        => track.IsHearingImpaired
            || (track.Title is { } title && (title.Contains("SDH", StringComparison.OrdinalIgnoreCase)
                || title.Contains("hearing", StringComparison.OrdinalIgnoreCase)));

    private static bool IsNotProgrammeAudio(MediaTrack track)
        => track.Title is { } title && NotProgrammeAudio.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));

    // Among tracks in one language: the default one, else the one with the most channels, else the first.
    private static MediaTrack? Best(IEnumerable<MediaTrack> tracks)
        => tracks.OrderByDescending(t => t.IsDefault).ThenByDescending(t => t.Channels).ThenBy(t => t.Index).FirstOrDefault();

    private static string Describe(IEnumerable<MediaTrack> tracks)
        => string.Join(", ", tracks.Select(t => Languages.Name(t.Language)).Distinct());
}
