using System.Text.Json;
using Jellyfin.Plugin.LinearTv.Configuration;
using Jellyfin.Plugin.LinearTv.Import;

namespace Jellyfin.Plugin.LinearTv.Tests;

public class ChannelImporterTests
{
    private static readonly Guid Inception = G(1);
    private static readonly Guid Interstellar = G(2);
    private static readonly Guid Thing1982 = G(3);
    private static readonly Guid Thing2011 = G(4);
    private static readonly Guid Amelie = G(5);
    private static readonly Guid AlienEarth = G(6);
    private static readonly Guid Sopranos = G(7);
    private static readonly Guid LawAndOrder = G(8);
    private static readonly Guid NolanCollection = G(9);
    private static readonly Guid MoviesLibrary = G(10);
    private static readonly Guid SopranosS01E01 = G(11);
    private static readonly Guid SopranosS01E02 = G(12);

    private static readonly LibraryCatalogue Library = new(
    [
        new(Inception, ContentKind.Movie, "Inception", Year: 2010),
        new(Interstellar, ContentKind.Movie, "Interstellar", Year: 2014),
        new(Thing1982, ContentKind.Movie, "The Thing", Year: 1982),
        new(Thing2011, ContentKind.Movie, "The Thing", Year: 2011),
        new(Amelie, ContentKind.Movie, "Amélie", OriginalTitle: "Le Fabuleux Destin d'Amélie Poulain", Year: 2001),
        new(AlienEarth, ContentKind.Series, "Alien: Earth", Year: 2025),
        new(Sopranos, ContentKind.Series, "The Sopranos", Year: 1999),
        new(LawAndOrder, ContentKind.Series, "Law & Order", Year: 1990),
        new(NolanCollection, ContentKind.Collection, "Christopher Nolan Collection"),
        new(MoviesLibrary, ContentKind.Library, "Movies"),
        new(SopranosS01E01, ContentKind.Episode, "The Sopranos", SeriesId: Sopranos, SeriesName: "The Sopranos", Season: 1, Episode: 1),
        new(SopranosS01E02, ContentKind.Episode, "46 Long", SeriesId: Sopranos, SeriesName: "The Sopranos", Season: 1, Episode: 2),

        // Real TMDb titles that tripped the first test against a real library.
        new(G(13), ContentKind.Movie, "Alien", Year: 1979),
        new(G(14), ContentKind.Movie, "Alien³", Year: 1992),
        new(G(15), ContentKind.Movie, "Die Hard", Year: 1988),
        new(G(16), ContentKind.Movie, "Die Hard 2", Year: 1990),
        new(G(17), ContentKind.Movie, "Bram Stoker's Dracula", Year: 1992),
    ]);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    private static (CatalogueItem? Item, string? Problem, string? Note) Resolve(ContentRef reference)
        => ChannelImporter.ResolveReference(reference, Library);

    // ---- matching ----

    [Theory]
    [InlineData("movie", "Inception", 2010)]
    [InlineData("MOVIE", "inception", null)]
    [InlineData("film", "Inception", null)]
    public void Movie_MatchesByTitle_CaseAndTypeAliasesIgnored(string type, string title, int? year)
        => Assert.Equal(Inception, Resolve(new() { Type = type, Title = title, Year = year }).Item?.Id);

    [Theory]
    [InlineData("series", "alien earth", 6)]          // punctuation
    [InlineData("show", "Alien - Earth", 6)]          // dash for colon, and an alias
    [InlineData("series", "Law and Order", 8)]        // & versus and
    [InlineData("movie", "Amelie", 5)]                // accents
    [InlineData("movie", "Le Fabuleux Destin d'Amelie Poulain", 5)] // original title
    public void Titles_IgnorePunctuationAccentsAndAmpersands(string type, string title, int expected)
        => Assert.Equal(G(expected), Resolve(new() { Type = type, Title = title }).Item?.Id);

    [Theory]
    [InlineData("Alien 3", null, 14)]  // superscripts are digits, not dropped
    [InlineData("Alien", null, 13)]    // ...so "Alien³" doesn't make plain "Alien" ambiguous
    public void Superscripts_CountAsDigits(string title, int? year, int expected)
        => Assert.Equal(G(expected), Resolve(new() { Type = "movie", Title = title, Year = year }).Item?.Id);

    [Theory]
    [InlineData("Die Hard 2: Die Harder", 16)]  // the library's title is shorter
    [InlineData("Dracula", 17)]                 // the library's title is longer
    public void PartialTitle_WithTheRightYear_Matches_WithANote(string title, int expected)
    {
        var (item, _, note) = Resolve(new() { Type = "movie", Title = title, Year = expected == 16 ? 1990 : 1992 });

        Assert.Equal(G(expected), item?.Id);
        Assert.Contains("partial title match", note);
    }

    [Fact]
    public void PartialTitle_WithoutAYear_IsOnlySuggested()
    {
        var (item, problem, _) = Resolve(new() { Type = "movie", Title = "Dracula" });

        Assert.Null(item);
        Assert.Contains("did you mean Bram Stoker's Dracula (1992)", problem);
    }

    [Fact]
    public void PartialTitle_WithTheWrongYear_IsNotGuessed()
        => Assert.Null(Resolve(new() { Type = "movie", Title = "Die Hard 2: Die Harder", Year = 1988 }).Item);

    [Fact]
    public void Year_OffByOne_StillMatches_WithANote()
    {
        var (item, problem, note) = Resolve(new() { Type = "movie", Title = "Inception", Year = 2011 });

        Assert.Equal(Inception, item?.Id);
        Assert.Null(problem);
        Assert.Contains("2010", note);
    }

    [Fact]
    public void Year_FarOff_IsAProblem_NamingWhatWasFound()
    {
        var (item, problem, _) = Resolve(new() { Type = "movie", Title = "Inception", Year = 1995 });

        Assert.Null(item);
        Assert.Contains("Inception (2010)", problem);
    }

    [Fact]
    public void SharedTitle_WithoutYear_IsAmbiguous_ListingBoth()
    {
        var (item, problem, _) = Resolve(new() { Type = "movie", Title = "The Thing" });

        Assert.Null(item);
        Assert.Contains("The Thing (1982)", problem);
        Assert.Contains("The Thing (2011)", problem);
    }

    [Theory]
    [InlineData(1982, 3)]
    [InlineData(2011, 4)]
    public void SharedTitle_WithYear_PicksTheRightOne(int year, int expected)
        => Assert.Equal(G(expected), Resolve(new() { Type = "movie", Title = "The Thing", Year = year }).Item?.Id);

    [Fact]
    public void NotFound_SuggestsCloseTitles()
    {
        var (item, problem, _) = Resolve(new() { Type = "movie", Title = "Interstella" });

        Assert.Null(item);
        Assert.Contains("did you mean Interstellar (2014)", problem);
    }

    [Fact]
    public void Kind_IsRespected_ButTheRightTypeIsPointedOut()
    {
        var (item, problem, _) = Resolve(new() { Type = "movie", Title = "the sopranos" });

        Assert.Null(item);
        Assert.Equal("no movie of that name, but The Sopranos (1999) is a series", problem);
    }

    [Theory]
    [InlineData("collection", "Christopher Nolan Collection", 9)]
    [InlineData("library", "movies", 10)]
    public void CollectionsAndLibraries_MatchByName(string type, string title, int expected)
        => Assert.Equal(G(expected), Resolve(new() { Type = type, Title = title }).Item?.Id);

    [Fact]
    public void Episode_ResolvesBySeriesSeasonAndNumber()
        => Assert.Equal(SopranosS01E02, Resolve(new() { Type = "episode", Series = "the sopranos", Season = 1, Episode = 2 }).Item?.Id);

    [Fact]
    public void Episode_NotInTheLibrary_IsAProblem()
    {
        var (item, problem, _) = Resolve(new() { Type = "episode", Series = "The Sopranos", Season = 6, Episode = 21 });

        Assert.Null(item);
        Assert.Contains("S06E21", problem);
    }

    [Theory]
    [InlineData(null, "missing type")]
    [InlineData("podcast", "unknown type 'podcast'")]
    public void Type_MissingOrUnknown_IsAProblem(string? type, string expected)
        => Assert.Contains(expected, Resolve(new() { Type = type, Title = "Inception" }).Problem);

    // ---- ids ----

    [Fact]
    public void Id_OnThisServer_IsUsedDirectly()
        => Assert.Equal(Inception, Resolve(new() { Id = Inception.ToString("N") }).Item?.Id);

    [Fact]
    public void Id_FromAnotherServer_FallsBackToTheTitle_WithANote()
    {
        // Ids derive from file paths, so the same film has a different id on another server.
        var (item, _, note) = Resolve(new() { Id = Guid.NewGuid().ToString("N"), Type = "movie", Title = "Inception" });

        Assert.Equal(Inception, item?.Id);
        Assert.Contains("matched by name", note);
    }

    [Fact]
    public void Id_UnknownWithNoTitle_IsAProblem()
        => Assert.Contains("no item with id", Resolve(new() { Id = Guid.NewGuid().ToString("N") }).Problem);

    [Fact]
    public void Id_OfTheWrongKind_IsAProblem()
        => Assert.Contains("is a series", Resolve(new() { Id = Sopranos.ToString("N"), Type = "movie" }).Problem);

    // ---- documents ----

    [Fact]
    public void Document_ResolvesChannels_InOrder_AndDeduplicates()
    {
        var (report, channels) = ChannelImporter.Resolve(Doc(Channel("201", "Nolan",
            new() { Type = "movie", Title = "Inception" },
            new() { Type = "movie", Title = "Interstellar" },
            new() { Type = "film", Title = "inception" })), Library);

        Assert.True(report.Ok);
        Assert.Equal([Inception, Interstellar], channels.Single().SourceIds);
        Assert.Single(report.Channels.Single().Warnings);
    }

    [Fact]
    public void Document_WithAnyUnresolvedContent_IsNotOk_AndSaysWhy()
    {
        var (report, _) = ChannelImporter.Resolve(Doc(Channel("201", "Nolan",
            new() { Type = "movie", Title = "Inception" },
            new() { Type = "movie", Title = "Oppenheimer" })), Library);

        Assert.False(report.Ok);
        Assert.Contains(report.Channels.Single().Problems, p => p.Contains("Oppenheimer") && p.Contains("not found"));
    }

    [Fact]
    public void Document_Problems_DuplicateNumbersMissingNamesNoContent()
    {
        var (report, _) = ChannelImporter.Resolve(Doc(
            Channel("201", "A", new ContentRef { Type = "movie", Title = "Inception" }),
            Channel("201", "B", new ContentRef { Type = "movie", Title = "Inception" }),
            Channel("202", " "),
            Channel(null, "D", new ContentRef { Type = "movie", Title = "Inception" })), Library);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, e => e.Contains("201 appears more than once"));
        Assert.Contains(report.Channels[2].Problems, p => p.Contains("Missing channel name"));
        Assert.Contains(report.Channels[2].Problems, p => p.Contains("No content"));
        Assert.Contains(report.Channels[3].Problems, p => p.Contains("Missing channel number"));
    }

    [Fact]
    public void Document_Empty_IsAnError()
        => Assert.False(ChannelImporter.Resolve(new ChannelDocument(), Library).Report.Ok);

    // ---- merging ----

    private static ChannelDefinition Existing(string number, string name, params Guid[] sources)
        => new() { Number = number, Name = name, SourceIds = [.. sources] };

    [Fact]
    public void Merge_SameNumber_ReplacesButKeepsTheChannelsIdentity()
    {
        var existing = new List<ChannelDefinition> { Existing("101", "Old", Inception), Existing("102", "Keep", Amelie) };
        var (report, imported) = ChannelImporter.Resolve(Doc(Channel("101", "New", new ContentRef { Type = "movie", Title = "Interstellar" })), Library);

        var merged = ChannelImporter.Merge(existing, imported, ImportMode.Merge, report);

        Assert.Equal(["101", "102"], merged.Select(c => c.Number));
        Assert.Equal(existing[0].Id, merged[0].Id);
        Assert.Equal("New", merged[0].Name);
        Assert.Equal([Interstellar], merged[0].SourceIds);
        Assert.Equal("replace", report.Channels.Single().Action);
    }

    [Fact]
    public void Merge_NewNumber_IsAppendedAsACreate()
    {
        var existing = new List<ChannelDefinition> { Existing("101", "Old", Inception) };
        var (report, imported) = ChannelImporter.Resolve(Doc(Channel("300", "New", new ContentRef { Type = "movie", Title = "Amelie" })), Library);

        var merged = ChannelImporter.Merge(existing, imported, ImportMode.Merge, report);

        Assert.Equal(["101", "300"], merged.Select(c => c.Number));
        Assert.Equal("create", report.Channels.Single().Action);
    }

    [Fact]
    public void Merge_NeverModifiesTheExistingChannels_SoDryRunsAreHarmless()
    {
        var existing = new List<ChannelDefinition> { Existing("101", "Old", Inception) };
        var (report, imported) = ChannelImporter.Resolve(Doc(Channel("101", "New", new ContentRef { Type = "movie", Title = "Amelie" })), Library);

        ChannelImporter.Merge(existing, imported, ImportMode.Merge, report);

        Assert.Equal("Old", existing[0].Name);
        Assert.Equal([Inception], existing[0].SourceIds);
    }

    [Fact]
    public void Replace_KeepsOnlyTheImportedChannels_AndReportsTheRemoved()
    {
        var existing = new List<ChannelDefinition> { Existing("101", "Gone", Inception), Existing("102", "Kept", Amelie) };
        var (report, imported) = ChannelImporter.Resolve(Doc(Channel("102", "Kept", new ContentRef { Type = "movie", Title = "Amelie" })), Library);

        var merged = ChannelImporter.Merge(existing, imported, ImportMode.Replace, report);

        Assert.Equal(["102"], merged.Select(c => c.Number));
        Assert.Equal(existing[1].Id, merged[0].Id);
        Assert.Equal(["101 Gone"], report.Removed);
        Assert.Equal("unchanged", report.Channels.Single().Action);
    }

    // ---- export and the format ----

    [Fact]
    public void ExportThenImport_ChangesNothing()
    {
        var existing = new List<ChannelDefinition>
        {
            Existing("101", "Films", Inception, Thing1982, Amelie, NolanCollection),
            new() { Number = "102", Name = "Telly", Shuffle = true, SourceIds = [Sopranos, LawAndOrder, SopranosS01E02, MoviesLibrary] },
        };

        var json = JsonSerializer.Serialize(ChannelExporter.Export(existing, Library), ChannelDocument.JsonOptions);
        var document = JsonSerializer.Deserialize<ChannelDocument>(json, ChannelDocument.JsonOptions)!;
        var (report, imported) = ChannelImporter.Resolve(document, Library);
        var merged = ChannelImporter.Merge(existing, imported, ImportMode.Merge, report);

        Assert.True(report.Ok);
        Assert.All(report.Channels, c => Assert.Equal("unchanged", c.Action));
        Assert.Equal(existing.SelectMany(c => c.SourceIds), merged.SelectMany(c => c.SourceIds));
    }

    [Fact]
    public void Export_NamesEachItem_SoAnotherServerCanFindIt()
    {
        var document = ChannelExporter.Export([Existing("101", "Mixed", Thing2011, SopranosS01E01)], Library);
        var content = document.Channels.Single().Content;

        Assert.Equal(("movie", "The Thing", 2011), (content[0].Type, content[0].Title, content[0].Year));
        Assert.Equal(("episode", "The Sopranos", 1, 1), (content[1].Type, content[1].Series, content[1].Season, content[1].Episode));
    }

    [Fact]
    public void Format_IsLenient_AboutCasingCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // written by hand, or by an AI that likes comments
              "Channels": [
                { "NUMBER": "201", "name": "Nolan", "content": [ { "type": "movie", "title": "Inception", }, ], },
              ],
            }
            """;

        var document = JsonSerializer.Deserialize<ChannelDocument>(json, ChannelDocument.JsonOptions)!;

        Assert.True(ChannelImporter.Resolve(document, Library).Report.Ok);
    }

    private static ChannelDocument Doc(params ChannelSpec[] channels) => new() { Channels = [.. channels] };

    private static ChannelSpec Channel(string? number, string name, params ContentRef[] content)
        => new() { Number = number, Name = name, Content = [.. content] };
}
