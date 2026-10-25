using LinearTv.Builder.Library;

namespace LinearTv.Builder.Tests;

public class TrackSelectorTests
{
    private const string Eng = "eng";
    private const string Jpn = "jpn";

    private static MediaTrack Audio(int index, string? language, string? title = null, bool isDefault = false, int channels = 2, bool original = false)
        => new() { Index = index, Codec = "aac", Language = language, Title = title, IsDefault = isDefault, Channels = channels, IsOriginal = original };

    private static MediaTrack Sub(int index, string? language, string? title = null, bool forced = false, bool sdh = false, bool text = true)
        => new() { Index = index, Codec = text ? "subrip" : "hdmv_pgs_subtitle", Language = language, Title = title, IsForced = forced, IsHearingImpaired = sdh, IsText = text };

    private static SourceItem Film(MediaTrack[] audio, params MediaTrack[] subtitles) => new()
    {
        Id = Guid.NewGuid(),
        Type = "Movie",
        Name = "Film",
        AudioTracks = audio,
        SubtitleTracks = subtitles,
    };

    private static TrackChoice Choose(SourceItem item, string? original, TrackOptions? options = null)
        => TrackSelector.Choose(item, original, options ?? TrackOptions.Default, Eng);

    // ---- audio ----

    [Fact]
    public void ForeignFilm_TakesTheOriginalLanguage_NotTheEnglishDub()
    {
        var film = Film([Audio(1, Eng, isDefault: true), Audio(2, Jpn)], Sub(3, Eng));

        Assert.Equal(2, Choose(film, Jpn).Audio!.Index);
    }

    [Fact]
    public void AskingForALanguage_AllowsTheDub()
    {
        var film = Film([Audio(1, Jpn), Audio(2, Eng)]);

        var choice = Choose(film, Jpn, new TrackOptions(Audio: Eng));

        Assert.Equal(2, choice.Audio!.Index);
        Assert.Null(choice.Subtitle); // English audio: nothing to burn
    }

    [Fact]
    public void AskingForALanguageTheFileLacks_LeavesTheItemOut()
        => Assert.Contains("no German audio", Choose(Film([Audio(1, Jpn)]), Jpn, new TrackOptions(Audio: "deu")).Skip);

    [Fact]
    public void OnlyADub_LeavesTheItemOut_AndSaysHowToAllowIt()
    {
        var skip = Choose(Film([Audio(1, Eng)]), Jpn).Skip;

        Assert.Contains("no Japanese (original) audio", skip);
        Assert.Contains("\"audio\"", skip);
    }

    [Fact]
    public void AnUntaggedTrack_CountsAsTheOriginal()
        => Assert.Equal(1, Choose(Film([Audio(1, null), Audio(2, Eng)], Sub(3, Eng)), Jpn).Audio!.Index);

    [Fact]
    public void TheFilesOriginalFlag_WinsOverEverything()
        => Assert.Equal(2, Choose(Film([Audio(1, Eng), Audio(2, "fra", original: true)], Sub(3, Eng)), Jpn).Audio!.Index);

    [Fact]
    public void OriginalUnknown_TakesTheFirstTrack()
        => Assert.Equal(1, Choose(Film([Audio(1, "fra"), Audio(2, Eng)], Sub(3, Eng)), null).Audio!.Index);

    [Fact]
    public void CommentaryAndDescriptionTracks_AreNeverChosen()
    {
        var film = Film([Audio(1, Eng, "Director's Commentary", isDefault: true), Audio(2, Eng, "Audio Description"), Audio(3, Eng)]);

        Assert.Equal(3, Choose(film, Eng).Audio!.Index);
    }

    [Fact]
    public void AmongTracksInTheRightLanguage_PrefersDefaultThenMostChannels()
    {
        var film = Film([Audio(1, Jpn, channels: 2), Audio(2, Jpn, channels: 6), Audio(3, Eng)], Sub(4, Eng));

        Assert.Equal(2, Choose(film, Jpn).Audio!.Index);
    }

    [Fact]
    public void NoAudioAtAll_IsSilence_NotALeftOutItem()
    {
        var choice = Choose(Film([]), Eng);

        Assert.Null(choice.Skip);
        Assert.Null(choice.Audio);
    }

    // ---- subtitles ----

    [Fact]
    public void ForeignAudio_BurnsFullSubtitles_PreferringNonSdh()
    {
        var film = Film([Audio(1, Jpn)], Sub(2, Eng, "SDH", sdh: true), Sub(3, Eng, "Signs", forced: true), Sub(4, Eng, "English"));

        Assert.Equal(4, Choose(film, Jpn).Subtitle!.Index);
    }

    [Fact]
    public void ForeignAudio_FallsBackToSdh_WhenThatsAllThereIs()
        => Assert.Equal(2, Choose(Film([Audio(1, Jpn)], Sub(2, Eng, "SDH", sdh: true)), Jpn).Subtitle!.Index);

    [Fact]
    public void ForeignAudio_WithNoSubtitlesInTheViewersLanguage_LeavesTheItemOut()
        => Assert.Contains("Japanese audio with no English subtitles", Choose(Film([Audio(1, Jpn)], Sub(2, "fra")), Jpn).Skip);

    [Fact]
    public void ForeignAudio_ForcedSubtitlesAlone_DoNotCount()
        => Assert.NotNull(Choose(Film([Audio(1, Jpn)], Sub(2, Eng, forced: true)), Jpn).Skip);

    [Fact]
    public void EnglishAudio_BurnsForcedSubtitles_OnlyIfThereAreAny()
    {
        Assert.Equal(3, Choose(Film([Audio(1, Eng)], Sub(2, Eng), Sub(3, Eng, forced: true)), Eng).Subtitle!.Index);
        Assert.Null(Choose(Film([Audio(1, Eng)], Sub(2, Eng)), Eng).Subtitle);
    }

    [Fact]
    public void AForcedTrackWithoutTheFlag_IsFoundByItsTitle()
        => Assert.Equal(3, Choose(Film([Audio(1, Eng)], Sub(2, Eng, "English"), Sub(3, Eng, "English (Forced)")), Eng).Subtitle!.Index);

    [Fact]
    public void ForcedSubtitlesInAnotherLanguage_AreIgnored()
        => Assert.Null(Choose(Film([Audio(1, Eng)], Sub(2, "fra", forced: true)), Eng).Subtitle);

    [Fact]
    public void SubtitlesNone_BurnsNothing_EvenForForeignAudio()
    {
        var choice = Choose(Film([Audio(1, Jpn)]), Jpn, new TrackOptions(Subtitles: SubtitleMode.None));

        Assert.Null(choice.Skip);
        Assert.Null(choice.Subtitle);
    }

    [Fact]
    public void SubtitlesFull_BurnsThemForEnglishAudioToo()
        => Assert.Equal(2, Choose(Film([Audio(1, Eng)], Sub(2, Eng)), Eng, new TrackOptions(Subtitles: SubtitleMode.Full)).Subtitle!.Index);

    [Fact]
    public void SubtitlesForced_NeverBurnsFullOnes_EvenForForeignAudio()
        => Assert.Null(Choose(Film([Audio(1, Jpn)], Sub(2, Eng)), Jpn, new TrackOptions(Subtitles: SubtitleMode.Forced)).Subtitle);

    [Fact]
    public void UntaggedAudio_InAForeignFilm_IsTreatedAsForeign()
        => Assert.Equal(2, Choose(Film([Audio(1, null)], Sub(2, Eng)), Jpn).Subtitle!.Index);

    [Fact]
    public void UntaggedAudio_OriginalUnknown_IsNotTreatedAsForeign()
        => Assert.Null(Choose(Film([Audio(1, null)], Sub(2, Eng)), null).Subtitle);
}
