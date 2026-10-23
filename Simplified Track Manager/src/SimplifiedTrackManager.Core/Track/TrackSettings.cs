using System.Globalization;
using System.Text.RegularExpressions;

namespace SimplifiedTrackManager.Core.Track;

/// <summary>
/// Everything the user decides about a track: who it is, what it is called,
/// its numbers, its crowd and pit lane, its ERS, and pictures to put in place
/// of the zip's own.
///
/// The names become language keys built on the track's three initials, the
/// way the game names its own: <c>lng_track_&lt;xxx&gt;_title</c>, <c>lng_stat_&lt;xxx&gt;_corners</c>,
/// <c>lng_aero_bias_&lt;xxx&gt;</c>. Each is written in English and used in every language.
/// </summary>
public sealed partial class TrackSettings
{
    // ── Identity ────────────────────────────────────────────────────────────
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    /// <summary>Three letters, the infix of every language key and the acronym the menus show.</summary>
    public string Initials { get; set; } = "";

    // ── Names ───────────────────────────────────────────────────────────────
    /// <summary>The country or short title, e.g. <c>Turkey</c>.</summary>
    public string Country { get; set; } = "";

    /// <summary>The circuit's name, e.g. <c>ISTANBUL PARK</c>.</summary>
    public string Circuit { get; set; } = "";

    public string City { get; set; } = "";

    /// <summary>The Grand Prix, short, e.g. <c>TURKISH GRAND PRIX</c>.</summary>
    public string GrandPrix { get; set; } = "";

    public string GrandPrix2025 { get; set; } = "";
    public string GrandPrix2026 { get; set; } = "";
    public string GrandPrixPrevious { get; set; } = "";

    /// <summary>What the F2 menus call the event, e.g. <c>ISTANBUL</c>.</summary>
    public string F2Title { get; set; } = "";

    // ── Numbers ─────────────────────────────────────────────────────────────
    /// <summary>Kilometres, e.g. <c>5.338</c>.</summary>
    public string LengthKm { get; set; } = "";
    public string Corners { get; set; } = "";
    public string Laps { get; set; } = "";
    public string LapRecord { get; set; } = "";
    public string LapRecordHolder { get; set; } = "";
    public string F2LapRecord { get; set; } = "";
    public string F2LapRecordHolder { get; set; } = "";
    public string Difficulty { get; set; } = "Medium";
    public string AeroBias { get; set; } = "Medium";

    // ── Crowd and pit lane ──────────────────────────────────────────────────
    public string Nationality { get; set; } = "";

    /// <summary>The three crowd colours as the game stores them, signed ARGB; null leaves one as the base track has it.</summary>
    public int?[] CrowdColours { get; } = new int?[3];

    public bool PitMirrored { get; set; }

    // ── ERS, and pictures ───────────────────────────────────────────────────
    public List<(string Key, string Value)> Ers { get; set; } = [];

    /// <summary>PNGs to put in place of the zip's pictures, by <see cref="TrackPictures.Slot.Key"/>.</summary>
    public Dictionary<string, string> Pictures { get; } = new(StringComparer.Ordinal);

    public string Infix => Initials.Trim().ToLowerInvariant();
    public string Acronym => Initials.Trim().ToUpperInvariant();

    /// <summary>The language keys and their English text, in a fixed order.</summary>
    public IReadOnlyList<(string Key, string Value)> LanguageKeys()
    {
        var x = Infix;
        return
        [
            ($"lng_track_{x}_title", Country),
            ($"lng_track_{x}_subtitle", Circuit),
            ($"lng_track_{x}_city_name", City),
            ($"lng_track_{x}_tla", Acronym),
            ($"lng_track_{x}_gp_title", GrandPrix2025),
            ($"lng_track_{x}_gp_title_multiline", GrandPrix2025),
            ($"lng_track_{x}_gp_title_26", GrandPrix2026),
            ($"lng_track_{x}_gp_title_multiline_26", GrandPrix2026),
            ($"lng_track_{x}_gp_title_multiline_previous", GrandPrixPrevious),
            ($"lng_track_{x}_gp_title_short", GrandPrix),
            ($"lng_track_{x}_f2_title", F2Title),
            ($"lng_track_{x}_lap_record_holder", Or(LapRecordHolder)),
            ($"lng_track_{x}_lap_record_holder_f2", Or(F2LapRecordHolder)),
            ($"lng_stat_{x}_circuit_length", LengthText()),
            ($"lng_stat_{x}_corners", Corners),
            ($"lng_stat_{x}_num_laps", Laps),
            ($"lng_stat_{x}_difficulty", Difficulty),
            ($"lng_stat_{x}_lap_record", Or(LapRecord)),
            ($"lng_stat_{x}_lap_record_f2", Or(F2LapRecord)),
            ($"lng_aero_bias_{x}", AeroBias),
        ];
    }

    /// <summary>
    /// The Track row's fields and what to write in each. Text fields name the
    /// language keys; the rest are the values themselves.
    /// </summary>
    public IReadOnlyList<(string Field, string Value)> TrackRowFields()
    {
        var x = Infix;
        var fields = new List<(string, string)>
        {
            ("title", $"lng_track_{x}_title"),
            ("country_name", $"lng_track_{x}_title"),
            ("circuit", $"lng_track_{x}_subtitle"),
            ("city_name", $"lng_track_{x}_city_name"),
            ("city_name_short", $"lng_track_{x}_city_name"),
            ("acronym_name", $"lng_track_{x}_tla"),
            ("gp_long_name", $"lng_track_{x}_gp_title"),
            ("gp_long_name_multiline", $"lng_track_{x}_gp_title_multiline"),
            ("gp_long_name_multiline_previous", $"lng_track_{x}_gp_title_multiline_previous"),
            ("gp_short_name", $"lng_track_{x}_gp_title_short"),
            ("aero_bias", $"lng_aero_bias_{x}"),
            ("TrackStats_0.SingleTrackStat_0.value", $"lng_stat_{x}_difficulty"),
            ("TrackStats_0.SingleTrackStat_1.value", $"lng_stat_{x}_num_laps"),
            ("TrackStats_0.SingleTrackStat_2.value", $"lng_stat_{x}_corners"),
            ("TrackStats_0.SingleTrackStat_3.value", $"lng_stat_{x}_circuit_length"),
            ("TrackStats_0.SingleTrackStat_4.value", $"lng_stat_{x}_lap_record"),
            ("TrackStats_0.SingleTrackStat_5.value", $"lng_track_{x}_lap_record_holder"),
            ("pitMirrored", PitMirrored ? "True" : "False"),
        };
        if (LengthMetres() is { } metres) fields.Add(("length_metres", metres.ToString(CultureInfo.InvariantCulture)));
        if (int.TryParse(Corners, NumberStyles.Integer, CultureInfo.InvariantCulture, out var corners))
            fields.Add(("numberOfCorners", corners.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < 3; i++)
            if (CrowdColours[i] is { } colour)
                fields.Add(($"TrackCrowds_0.crowdColour{i + 1}", colour.ToString(CultureInfo.InvariantCulture)));
        if (Nationality.Trim().Length > 0) fields.Add(("nationality", Nationality.Trim()));
        return fields;
    }

    /// <summary>
    /// The fields of the track's TrackData row in <paramref name="dataset"/>.
    /// F2 datasets name the event by its F2 title, 2026 ones by the 2026 Grand
    /// Prix, and the classic dataset by the short name — as the game's own
    /// tracks do.
    /// </summary>
    public IReadOnlyList<(string Field, string Value)> TrackDataFields(string dataset)
    {
        var x = Infix;
        var f2 = dataset.StartsWith("f2", StringComparison.Ordinal);
        var y26 = dataset.Contains("2026", StringComparison.Ordinal);
        var classic = dataset == "classic";
        var f2Title = $"lng_track_{x}_f2_title";
        return
        [
            ("TrackNameData_0.title", $"lng_track_{x}_title"),
            ("TrackNameData_0.country_name", $"lng_track_{x}_title"),
            ("TrackNameData_0.circuit", $"lng_track_{x}_subtitle"),
            ("TrackNameData_0.city_name", $"lng_track_{x}_city_name"),
            ("TrackNameData_0.city_name_short", $"lng_track_{x}_city_name"),
            ("TrackNameData_0.acronym_name", $"lng_track_{x}_tla"),
            ("TrackNameData_0.gp_short_name", f2 ? f2Title : $"lng_track_{x}_gp_title_short"),
            ("TrackNameData_0.gp_long_name", f2 ? f2Title : classic ? $"lng_track_{x}_gp_title_short"
                                             : y26 ? $"lng_track_{x}_gp_title_26" : $"lng_track_{x}_gp_title"),
            ("TrackNameData_0.gp_long_name_multiline", f2 ? f2Title
                                             : y26 ? $"lng_track_{x}_gp_title_multiline_26" : $"lng_track_{x}_gp_title_multiline"),
            ("TrackNameData_0.gp_long_name_multiline_previous", f2 ? f2Title
                                             : y26 ? $"lng_track_{x}_gp_title_multiline" : $"lng_track_{x}_gp_title_multiline_previous"),
            ("TrackStats_0.SingleTrackStat_0.value", f2 ? $"lng_stat_{x}_lap_record_f2" : $"lng_stat_{x}_lap_record"),
            ("TrackStats_0.SingleTrackStat_1.value", f2 ? $"lng_track_{x}_lap_record_holder_f2" : $"lng_track_{x}_lap_record_holder"),
        ];
    }

    /// <summary>
    /// What stops a build, one line each; empty when the settings are complete.
    /// </summary>
    public IReadOnlyList<string> Problems(StockGame game)
    {
        var problems = new List<string>();
        var code = Code.Trim();
        if (!CodePattern().IsMatch(code))
            problems.Add("The track code has to be lower-case letters, digits, _ or -, starting with a letter.");
        else if (game.IsStockTrack(code))
            problems.Add($"The game already has a track called {code}. Give this one a code of its own.");

        if (!InitialsPattern().IsMatch(Initials.Trim()))
            problems.Add("The initials have to be exactly three letters.");
        else if (game.TakenInitials.Contains(Infix))
            problems.Add($"{Acronym} belongs to one of the game's own tracks. Pick other initials.");

        if (Name.Trim().Length == 0) problems.Add("The track needs a name.");
        if (!VersionPattern().IsMatch(Version.Trim())) problems.Add("The version has to look like 1.0.0.");
        if (Country.Trim().Length == 0) problems.Add("The track needs a country or title for the menus.");
        if (LengthKm.Trim().Length > 0 && LengthMetres() is null) problems.Add("The length has to be a number of kilometres, like 5.338.");
        if (Corners.Trim().Length > 0 && !int.TryParse(Corners, out _)) problems.Add("The corners have to be a whole number.");

        foreach (var (key, value) in Ers)
            if (key.Trim().Length == 0 || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                problems.Add($"The ERS value {(key.Length > 0 ? key : "(unnamed)")} has to be a number.");
        return problems;
    }

    /// <summary>A code from a name: <c>Istanbul Park</c> becomes <c>istanbul_park</c>.</summary>
    public static string CodeFrom(string name)
    {
        var code = NotCodeChars().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        if (code.Length == 0 || !char.IsAsciiLetterLower(code[0])) code = "track_" + code;
        return code.Length > 40 ? code[..40].TrimEnd('_') : code;
    }

    /// <summary>A version as Swofty wants it, three numbers: <c>2.72</c> becomes <c>2.72.0</c>.</summary>
    public static string SemVer(string version)
    {
        var parts = Regex.Matches(version, @"\d+").Select(m => m.Value).Take(3).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Select(p => p.TrimStart('0') is { Length: > 0 } t ? t : "0"));
    }

    private int? LengthMetres() =>
        double.TryParse(LengthKm.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var km) && km > 0
            ? (int)Math.Round(km * 1000)
            : null;

    /// <summary>The stat line the game shows: <c>5.338 km/ 3.317 miles</c>.</summary>
    private string LengthText() =>
        LengthMetres() is { } metres
            ? string.Create(CultureInfo.InvariantCulture, $"{metres / 1000.0:0.000} km/ {metres / 1609.344:0.000} miles")
            : "-";

    private static string Or(string value) => value.Trim().Length > 0 ? value.Trim() : "-";

    [GeneratedRegex("^[a-z][a-z0-9_-]{1,39}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z]{3}$")]
    private static partial Regex InitialsPattern();

    /// <summary>Semver's three numbers, none with a leading zero — Swofty refuses <c>1.00.0</c>.</summary>
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotCodeChars();
}
