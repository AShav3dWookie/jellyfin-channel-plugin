namespace LinearTv.Builder.Library;

/// <summary>
/// Language codes as they appear in the wild, reduced to one canonical form (ISO 639-2/T, e.g.
/// <c>fra</c>) so they can be compared: stream tags use 639-2 in either its B or T form
/// (<c>fre</c>/<c>fra</c>), TMDb uses 639-1 (<c>fr</c>), and channel files may use either.
/// </summary>
internal static class Languages
{
    public const string English = "eng";

    // ISO 639-1, 639-2/T, 639-2/B where it differs, English name.
    private static readonly (string One, string T, string? B, string Name)[] Table =
    [
        ("en", "eng", null, "English"), ("fr", "fra", "fre", "French"), ("de", "deu", "ger", "German"),
        ("es", "spa", null, "Spanish"), ("it", "ita", null, "Italian"), ("ja", "jpn", null, "Japanese"),
        ("ko", "kor", null, "Korean"), ("zh", "zho", "chi", "Chinese"), ("ru", "rus", null, "Russian"),
        ("pt", "por", null, "Portuguese"), ("nl", "nld", "dut", "Dutch"), ("sv", "swe", null, "Swedish"),
        ("da", "dan", null, "Danish"), ("no", "nor", null, "Norwegian"), ("fi", "fin", null, "Finnish"),
        ("is", "isl", "ice", "Icelandic"), ("pl", "pol", null, "Polish"), ("cs", "ces", "cze", "Czech"),
        ("sk", "slk", "slo", "Slovak"), ("hu", "hun", null, "Hungarian"), ("ro", "ron", "rum", "Romanian"),
        ("bg", "bul", null, "Bulgarian"), ("el", "ell", "gre", "Greek"), ("tr", "tur", null, "Turkish"),
        ("he", "heb", null, "Hebrew"), ("ar", "ara", null, "Arabic"), ("fa", "fas", "per", "Persian"),
        ("hi", "hin", null, "Hindi"), ("ta", "tam", null, "Tamil"), ("te", "tel", null, "Telugu"),
        ("bn", "ben", null, "Bengali"), ("ur", "urd", null, "Urdu"), ("ml", "mal", null, "Malayalam"),
        ("kn", "kan", null, "Kannada"), ("mr", "mar", null, "Marathi"), ("pa", "pan", null, "Punjabi"),
        ("th", "tha", null, "Thai"), ("vi", "vie", null, "Vietnamese"), ("id", "ind", null, "Indonesian"),
        ("ms", "msa", "may", "Malay"), ("tl", "tgl", null, "Tagalog"), ("uk", "ukr", null, "Ukrainian"),
        ("sr", "srp", null, "Serbian"), ("hr", "hrv", null, "Croatian"), ("bs", "bos", null, "Bosnian"),
        ("sl", "slv", null, "Slovenian"), ("mk", "mkd", "mac", "Macedonian"), ("sq", "sqi", "alb", "Albanian"),
        ("et", "est", null, "Estonian"), ("lv", "lav", null, "Latvian"), ("lt", "lit", null, "Lithuanian"),
        ("ga", "gle", null, "Irish"), ("cy", "cym", "wel", "Welsh"), ("gd", "gla", null, "Scottish Gaelic"),
        ("ca", "cat", null, "Catalan"), ("eu", "eus", "baq", "Basque"), ("gl", "glg", null, "Galician"),
        ("af", "afr", null, "Afrikaans"), ("sw", "swa", null, "Swahili"), ("ka", "kat", "geo", "Georgian"),
        ("hy", "hye", "arm", "Armenian"), ("az", "aze", null, "Azerbaijani"), ("kk", "kaz", null, "Kazakh"),
        ("uz", "uzb", null, "Uzbek"), ("mn", "mon", null, "Mongolian"), ("be", "bel", null, "Belarusian"),
        ("km", "khm", null, "Khmer"), ("lo", "lao", null, "Lao"), ("my", "mya", "bur", "Burmese"),
        ("ne", "nep", null, "Nepali"), ("si", "sin", null, "Sinhala"), ("la", "lat", null, "Latin"),
        ("yi", "yid", null, "Yiddish"), ("am", "amh", null, "Amharic"), ("so", "som", null, "Somali"),
        ("zu", "zul", null, "Zulu"), ("xh", "xho", null, "Xhosa"), ("yo", "yor", null, "Yoruba"),
    ];

    // Codes that mean "no particular language", and equivalents that are better compared as one.
    private static readonly Dictionary<string, string?> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["und"] = null, ["zxx"] = null, ["mul"] = null, ["mis"] = null, ["xx"] = null,
        ["nob"] = "nor", ["nno"] = "nor", ["nb"] = "nor", ["nn"] = "nor",
        // TMDb's "cn" is Cantonese; files tag Chinese audio chi/zho/yue/cmn interchangeably.
        ["cn"] = "zho", ["yue"] = "zho", ["cmn"] = "zho",
    };

    private static readonly Dictionary<string, string> ByCode = BuildCodes();

    private static readonly Dictionary<string, string> ByName = Table.ToDictionary(l => l.Name, l => l.T, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> NameOf = Table.ToDictionary(l => l.T, l => l.Name);

    /// <summary>
    /// The first language of each production country, as TMDb and Jellyfin name countries.
    /// Countries without one clear film language (Belgium, Switzerland, India's many) are absent,
    /// so the next country listed decides instead.
    /// </summary>
    private static readonly Dictionary<string, string> CountryLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States of America"] = "eng", ["United States"] = "eng", ["USA"] = "eng", ["United Kingdom"] = "eng",
        ["Canada"] = "eng", ["Australia"] = "eng", ["New Zealand"] = "eng", ["Ireland"] = "eng",
        ["France"] = "fra", ["Germany"] = "deu", ["West Germany"] = "deu", ["East Germany"] = "deu", ["Austria"] = "deu",
        ["Spain"] = "spa", ["Mexico"] = "spa", ["Argentina"] = "spa", ["Chile"] = "spa", ["Colombia"] = "spa",
        ["Peru"] = "spa", ["Cuba"] = "spa", ["Uruguay"] = "spa", ["Venezuela"] = "spa",
        ["Italy"] = "ita", ["Japan"] = "jpn", ["South Korea"] = "kor", ["Korea, Republic of"] = "kor",
        ["China"] = "zho", ["Hong Kong"] = "zho", ["Taiwan"] = "zho",
        ["Russia"] = "rus", ["Russian Federation"] = "rus", ["Soviet Union"] = "rus",
        ["Brazil"] = "por", ["Portugal"] = "por", ["Netherlands"] = "nld", ["Sweden"] = "swe", ["Denmark"] = "dan",
        ["Norway"] = "nor", ["Finland"] = "fin", ["Iceland"] = "isl", ["Poland"] = "pol",
        ["Czech Republic"] = "ces", ["Czechia"] = "ces", ["Czechoslovakia"] = "ces", ["Slovakia"] = "slk",
        ["Hungary"] = "hun", ["Romania"] = "ron", ["Bulgaria"] = "bul", ["Greece"] = "ell", ["Turkey"] = "tur",
        ["Israel"] = "heb", ["Iran"] = "fas", ["Thailand"] = "tha", ["Vietnam"] = "vie", ["Indonesia"] = "ind",
        ["Ukraine"] = "ukr", ["Serbia"] = "srp", ["Croatia"] = "hrv", ["Slovenia"] = "slv", ["Estonia"] = "est",
        ["Latvia"] = "lav", ["Lithuania"] = "lit", ["Georgia"] = "kat", ["Armenia"] = "hye",
        ["Egypt"] = "ara", ["Saudi Arabia"] = "ara", ["Lebanon"] = "ara",
    };

    /// <summary>
    /// The canonical code for a language code or English name; null for "undetermined" and the
    /// like, and for anything not recognised.
    /// </summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var code = language.Trim();
        if (Special.TryGetValue(code, out var special))
        {
            return special;
        }

        return ByCode.TryGetValue(code, out var t) ? t : ByName.GetValueOrDefault(code);
    }

    /// <summary>Whether <paramref name="language"/> is a code or name this table knows.</summary>
    public static bool IsKnown(string language) => Normalize(language) is not null;

    /// <summary>The language of the first production country that has a clear one.</summary>
    public static string? FromCountries(IEnumerable<string> countries)
        => countries.Select(c => CountryLanguage.GetValueOrDefault(c.Trim())).FirstOrDefault(l => l is not null);

    /// <summary>For people: "Japanese", or the code itself if unknown.</summary>
    public static string Name(string? canonical)
        => canonical is null ? "undetermined" : NameOf.GetValueOrDefault(canonical, canonical);

    private static Dictionary<string, string> BuildCodes()
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (one, t, b, _) in Table)
        {
            codes[one] = t;
            codes[t] = t;
            if (b is not null)
            {
                codes[b] = t;
            }
        }

        return codes;
    }
}
