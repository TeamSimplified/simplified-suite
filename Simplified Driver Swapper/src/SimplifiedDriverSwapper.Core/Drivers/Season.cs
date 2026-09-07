using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedDriverSwapper.Core.Drivers;

/// <summary>
/// A season's grid: which entrant list holds it, which team owns each seat, and
/// who sits there by default.
///
/// The teams and seat counts were read out of the database — each entrant record
/// names its own <c>team</c> — but the default driver per seat is stored nowhere:
/// vanilla leaves every seat empty and the game fills the lineup in itself. So
/// that column is a table, and its order has to be confirmed against the game.
///
/// Kit record order is not a substitute. Checked against the confirmed 2026
/// grid it disagrees for Mercedes, Williams, Aston and Haas, and three teams
/// carry a spare record from a mid-season driver change.
/// </summary>
public sealed class Season
{
    /// <param name="Slot">Index within the entrant list.</param>
    /// <param name="Team">Entrant team id as stored, e.g. <c>mclaren_26</c>.</param>
    /// <param name="KitToken">
    /// Team token used in kit metadata (<c>2026-mclaren_lando_norris</c>). Not
    /// always the team id — 2026's ids carry a <c>_26</c> suffix the kit strings
    /// don't.
    /// </param>
    /// <param name="DefaultDriver">Who the game puts here when the seat is empty.</param>
    public sealed record Seat(int Slot, string Team, string KitToken, string DefaultDriver, string Year)
    {
        /// <summary>
        /// The metadata string identifying this seat's kit. A driver's helmet is
        /// moved onto this string to make the game show it here, so it has to
        /// match what the game actually has in the seat.
        /// </summary>
        public string LookupString => $"{Year}-{KitToken}_{DefaultDriver}";
    }

    private Season(string year, int entrantList, string listName, string label,
        (string Team, string Kit, string Driver)[] seats)
    {
        Year = year;
        EntrantList = entrantList;
        ListName = listName;
        Label = label;
        Seats = seats.Select((s, i) => new Seat(i, s.Team, s.Kit, s.Driver, year)).ToList();
    }

    /// <summary>Season these seats belong to. Also the prefix on kit metadata strings.</summary>
    public string Year { get; }

    /// <summary>Which <c>entrant_list_N</c> holds this grid.</summary>
    public int EntrantList { get; }

    /// <summary>
    /// The list's own <c>_metadata_</c> name — <c>f1_2026</c> and the like.
    ///
    /// The index above is not stable across game updates. The F2 26 patch
    /// inserted <c>f2_2026</c> at 20 and pushed every later list down one, which
    /// moved the 2026 grid from 33 to 34. An app trusting the old number would
    /// have written a grid's worth of edits over <c>f1_beta</c> and said nothing,
    /// so the number is checked against this name rather than believed.
    /// </summary>
    public string ListName { get; }

    /// <summary>
    /// Where this grid actually is in a given database: the list carrying
    /// <see cref="ListName"/>, or null when there isn't one.
    /// </summary>
    public int? FindIn(GameDatabase db)
    {
        var count = db.Int("entrants.entrant_lists._entrant_list_instancecount_");
        for (var i = 0; i < count; i++)
            if (db.Text($"entrants.entrant_lists.entrant_list_{i}._metadata_") == ListName)
                return i;
        return null;
    }

    /// <summary>True when <see cref="EntrantList"/> still points at this grid.</summary>
    public bool IsWhereExpected(GameDatabase db) =>
        db.Text($"entrants.entrant_lists.entrant_list_{EntrantList}._metadata_") == ListName;

    /// <summary>What to call it in the UI.</summary>
    public string Label { get; }

    public IReadOnlyList<Seat> Seats { get; }

    public override string ToString() => Label;

    public string SeatPath(int slot, string field) =>
        $"entrants.entrant_lists.entrant_list_{EntrantList}.entrant_{slot}.{field}";

    // ── The grids ───────────────────────────────────────────────────────────

    /// <summary>
    /// 2026, entrant list 34. Seat order confirmed in-game.
    ///
    /// It was list 33 until the F2 26 update inserted a list ahead of it.
    /// </summary>
    public static readonly Season F2026 = new("2026", 34, "f1_2026", "2026 grid",
    [
        ("mclaren_26",     "mclaren",    "lando_norris"),
        ("mclaren_26",     "mclaren",    "oscar_piastri"),
        ("mercedes_26",    "mercedes",   "george_russell"),
        ("mercedes_26",    "mercedes",   "andrea-kimi_antonelli"),
        ("redbull_26",     "redbull",    "max_verstappen"),
        ("redbull_26",     "redbull",    "isack_hadjar"),
        ("ferrari_26",     "ferrari",    "charles_leclerc"),
        ("ferrari_26",     "ferrari",    "lewis_hamilton"),
        ("williams_26",    "williams",   "alex_albon"),
        ("williams_26",    "williams",   "carlos_sainz-jr"),
        ("tororosso_26",   "tororosso",  "liam_lawson"),
        ("tororosso_26",   "tororosso",  "arvid_lindblad"),
        ("forceindia_26",  "forceindia", "fernando_alonso"),
        ("forceindia_26",  "forceindia", "lance_stroll"),
        ("haas_26",        "haas",       "oliver_bearman"),
        ("haas_26",        "haas",       "esteban_ocon"),
        ("sauber_26",      "sauber",     "nico_hulkenberg"),
        ("sauber_26",      "sauber",     "gabriel_bortoleto"),
        ("renault_26",     "renault",    "pierre_gasly"),
        ("renault_26",     "renault",    "franco_colapinto"),
        ("gm_cadillac_26", "cadillac",   "sergio_perez"),
        ("gm_cadillac_26", "cadillac",   "valtteri_bottas"),
    ]);

    /// <summary>
    /// 2025, entrant list 15. Twenty seats — no Cadillac — and the teams sit in
    /// the 2024 championship order rather than 2026's.
    ///
    /// Teams and slots come from the database; the drivers were confirmed
    /// in-game, which is the only way to get them, and which corrected McLaren
    /// and Aston from the order first assumed here.
    ///
    /// Those two corrections are also why kit record order can't be trusted as a
    /// shortcut: it happened to be right for Aston and wrong for McLaren.
    /// </summary>
    public static readonly Season F2025 = new("2025", 15, "f1_2025", "2025 grid",
    [
        ("mclaren",    "mclaren",    "oscar_piastri"),
        ("mclaren",    "mclaren",    "lando_norris"),
        ("ferrari",    "ferrari",    "charles_leclerc"),
        ("ferrari",    "ferrari",    "lewis_hamilton"),
        ("redbull",    "redbull",    "max_verstappen"),
        ("redbull",    "redbull",    "yuki_tsunoda"),
        ("mercedes",   "mercedes",   "george_russell"),
        ("mercedes",   "mercedes",   "andrea-kimi_antonelli"),
        ("forceindia", "forceindia", "lance_stroll"),
        ("forceindia", "forceindia", "fernando_alonso"),
        ("renault",    "renault",    "pierre_gasly"),
        ("renault",    "renault",    "franco_colapinto"),
        ("haas",       "haas",       "esteban_ocon"),
        ("haas",       "haas",       "oliver_bearman"),
        ("tororosso",  "tororosso",  "isack_hadjar"),
        ("tororosso",  "tororosso",  "liam_lawson"),
        ("williams",   "williams",   "alex_albon"),
        ("williams",   "williams",   "carlos_sainz-jr"),
        ("sauber",     "sauber",     "nico_hulkenberg"),
        ("sauber",     "sauber",     "gabriel_bortoleto"),
    ]);

    /// <summary>
    /// The 2026 Formula 2 grid, entrant list 20 — new in the F2 26 update.
    ///
    /// Teams, slot order and car numbers are the database's own. The driver
    /// column is not: as with the F1 grids the game leaves every seat empty and
    /// fills the lineup itself, so the pairing comes from the season's kit
    /// records, which name a team and a driver together.
    ///
    /// That gives the right two drivers per team but not which of them sits in
    /// the first seat, and the note at the top of this file applies — kit order
    /// disagreed with the real thing for four of the eleven 2026 F1 teams. So
    /// the within-team order here is the best reading of the files rather than
    /// something confirmed in-game, and getting it wrong swaps two team-mates'
    /// helmets, nothing worse.
    /// </summary>
    public static readonly Season F2_2026 = new("2026", 20, "f2_2026", "2026 F2 grid",
    [
        ("invicta_26",         "f2-invictaracing",     "joshua_duerksen"),
        ("invicta_26",         "f2-invictaracing",     "rafael_camara"),
        ("hitech_26",          "f2-hitech",            "colton_herta"),
        ("hitech_26",          "f2-hitech",            "ritomo_miyata"),
        ("campos_26",          "f2-camposracing",      "nikola_tsolov"),
        ("campos_26",          "f2-camposracing",      "noel_leon"),
        ("dams_26",            "f2-dams",              "dino_beganovic"),
        ("dams_26",            "f2-dams",              "roman_bilinski"),
        ("mpmotorsport_26",    "f2-mpmotorsport",      "gabriele_mini"),
        ("mpmotorsport_26",    "f2-mpmotorsport",      "oliver_goethe"),
        ("prema_26",           "f2-prema",             "mari_boya"),
        ("prema_26",           "f2-prema",             "sebastian_montoya"),
        // Rodin's driver carries two ids in this database, alex_dunne and
        // alexander_dunne, each with its own kit and helmet records. The short
        // one sits with its team-mate in the kit block; the long one was
        // appended after every other 2026 record, so it reads as the later
        // addition of the two.
        ("rodinmotorsport_26", "f2-rodin",             "alex_dunne"),
        ("rodinmotorsport_26", "f2-rodin",             "martinius_stenshorne"),
        ("artgp_26",           "f2-artgrandprix",      "kush_maini"),
        ("artgp_26",           "f2-artgrandprix",      "tasanapol_inthraphuvasak"),
        ("aixracing_26",       "f2-aixracing",         "cian_shields"),
        ("aixracing_26",       "f2-aixracing",         "emmo_fittipaldi-jr"),
        ("vanamersfoort_26",   "f2-vanamersfoort",     "nicolas_varrone"),
        ("vanamersfoort_26",   "f2-vanamersfoort",     "rafael_villagomez"),
        ("trident_26",         "f2-tridentmotorsport", "john_bennett"),
        ("trident_26",         "f2-tridentmotorsport", "laurens_vanhoepen"),
    ]);

    /// <summary>Selectable grids, newest first.</summary>
    public static readonly IReadOnlyList<Season> All = [F2026, F2025, F2_2026];

    // ── Paths that aren't tied to a season ──────────────────────────────────

    public static string DriverPath(int index, string field) =>
        $"entrants.drivers.driver_{index}.{field}";

    public static string HelmetPath(int index, string field) =>
        $"customisation.custom_avatar.helmets.Helmet_{index}.{field}";

    public static string KitPath(string directory, string className, int index, string field) =>
        $"customisation.custom_avatar.{directory}.{className}_{index}.{field}";

    public static string KitCountPath(string directory, string countField) =>
        $"customisation.custom_avatar.{directory}.{countField}";

    /// <summary>
    /// <c>carlos_sainz-jr</c> → <c>Carlos Sainz Jr</c>. Ids are the game's, not
    /// something to show a user as-is.
    /// </summary>
    public static string Pretty(string driverId)
    {
        var parts = driverId.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
