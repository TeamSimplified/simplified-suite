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

    private Season(string year, int entrantList, string label, (string Team, string Kit, string Driver)[] seats)
    {
        Year = year;
        EntrantList = entrantList;
        Label = label;
        Seats = seats.Select((s, i) => new Seat(i, s.Team, s.Kit, s.Driver, year)).ToList();
    }

    /// <summary>Season these seats belong to. Also the prefix on kit metadata strings.</summary>
    public string Year { get; }

    /// <summary>Which <c>entrant_list_N</c> holds this grid.</summary>
    public int EntrantList { get; }

    /// <summary>What to call it in the UI.</summary>
    public string Label { get; }

    public IReadOnlyList<Seat> Seats { get; }

    public override string ToString() => Label;

    public string SeatPath(int slot, string field) =>
        $"entrants.entrant_lists.entrant_list_{EntrantList}.entrant_{slot}.{field}";

    // ── The grids ───────────────────────────────────────────────────────────

    /// <summary>2026, entrant list 33. Seat order confirmed in-game.</summary>
    public static readonly Season F2026 = new("2026", 33, "2026 grid",
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
    public static readonly Season F2025 = new("2025", 15, "2025 grid",
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

    /// <summary>Selectable grids, newest first.</summary>
    public static readonly IReadOnlyList<Season> All = [F2026, F2025];

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
