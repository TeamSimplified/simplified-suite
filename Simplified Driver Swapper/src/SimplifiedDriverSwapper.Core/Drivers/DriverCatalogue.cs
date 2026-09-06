using System.Text.RegularExpressions;
using EgoEngineLibrary.Data.GameDatabases;

namespace SimplifiedDriverSwapper.Core.Drivers;

/// <summary>
/// Everything the editor can choose from in a given database: the drivers, and
/// the helmet / suit / glove records, read out of the file rather than assumed.
/// </summary>
public sealed partial class DriverCatalogue
{
    /// <param name="Id">Game id, e.g. <c>max_verstappen</c>.</param>
    /// <param name="Number">The driver's own car number, or <c>255</c> for none.</param>
    public sealed record DriverEntry(string Id, string Number)
    {
        public string Display => Number is "255" or ""
            ? Season.Pretty(Id)
            : $"{Season.Pretty(Id)}  (#{Number})";
    }

    /// <summary>A suit or glove record: where it sits, and the id other records point at.</summary>
    public sealed record KitEntry(int Index, string? Id);

    private DriverCatalogue(
        List<DriverEntry> drivers,
        Dictionary<string, int> helmets,
        Dictionary<string, KitEntry> suits,
        Dictionary<string, KitEntry> gloves,
        Dictionary<string, int> homeHelmets)
    {
        Drivers = drivers;
        Helmets = helmets;
        Suits = suits;
        Gloves = gloves;
        HomeHelmets = homeHelmets;
        Numbers = drivers.ToDictionary(d => d.Id, d => d.Number, StringComparer.Ordinal);
        HelmetMetaByIndex = helmets.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    /// <summary>
    /// The unbranded helmets the game ships — <c>tier01_livery76</c> and the
    /// like — used for drivers who have none of their own.
    ///
    /// Ordered by name so a given driver always lands on the same one. Roughly
    /// half the drivers in the game have no helmet, and there are far more of
    /// these than there are seats, so each can have a different one.
    /// </summary>
    public List<int> GenericHelmets { get; private init; } = [];

    /// <summary>True when this driver has a helmet of his own.</summary>
    public bool HasOwnHelmet(string driverId) => HomeHelmets.ContainsKey(driverId);

    /// <summary>His own helmet, or null when he hasn't got one.</summary>
    public int? OwnHelmet(string driverId) =>
        HomeHelmets.TryGetValue(driverId, out var own) ? own : null;

    /// <summary>Every real driver, sorted by id. Excludes the <c>default_driver</c> placeholder.</summary>
    public List<DriverEntry> Drivers { get; }

    public Dictionary<string, string> Numbers { get; }

    /// <summary>Helmet metadata → record index.</summary>
    public Dictionary<string, int> Helmets { get; }

    /// <summary>Helmet record index → metadata, for showing what a record currently holds.</summary>
    public Dictionary<int, string> HelmetMetaByIndex { get; }

    public Dictionary<string, KitEntry> Suits { get; }
    public Dictionary<string, KitEntry> Gloves { get; }

    /// <summary>Driver id → the helmet record that is most naturally "his".</summary>
    public Dictionary<string, int> HomeHelmets { get; }

    /// <summary>
    /// Helmet record index → the design id it points at. Helmets carry an
    /// <c>id</c> just as suits and gloves do, so a record can be pointed at
    /// another record's design without moving any metadata around.
    /// </summary>
    public Dictionary<int, string> HelmetIds { get; private init; } = [];

    /// <summary>The designs the generic helmets carry, for recognising one later.</summary>
    public HashSet<string> GenericHelmetDesigns =>
        GenericHelmets.Select(r => HelmetIds.GetValueOrDefault(r))
                      .Where(id => id is not null)
                      .ToHashSet(StringComparer.Ordinal)!;

    /// <summary>
    /// Design id → the name that design had before anything was edited.
    ///
    /// Assigning a helmet writes a seat's lookup string over the chosen record's
    /// metadata, so in an edited database a record is no longer named after what
    /// it carries. The untouched backup still is, which makes it the only way to
    /// say what a design is once the file has been saved.
    /// </summary>
    public Dictionary<string, string> DesignNames { get; private init; } = [];

    /// <summary>
    /// What to call the design on a helmet record: its original name when that
    /// can be recovered, otherwise whatever the record is currently labelled.
    /// </summary>
    public string HelmetName(int record) =>
        HelmetIds.TryGetValue(record, out var id) && DesignNames.TryGetValue(id, out var name)
            ? name
            : HelmetMetaByIndex.GetValueOrDefault(record, $"Helmet_{record}");

    /// <summary>Every helmet to choose from, named by design and sorted.</summary>
    public List<(string Name, int Record)> HelmetChoices() =>
        Helmets.Values
            .Select(record => (Name: HelmetName(record), Record: record))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

    /// <param name="reference">
    /// An untouched copy of the same database, used only to recover design names.
    /// </param>
    public static DriverCatalogue Read(GameDatabase db, GameDatabase? reference = null)
    {
        var drivers = ReadDrivers(db);
        var helmets = IndexKits(db, "helmets", "Helmet", "_Helmet_instancecount_");
        var suits = IndexKits(db, "suits", "Suit", "_Suit_instancecount_");
        var gloves = IndexKits(db, "gloves", "Gloves", "_Gloves_instancecount_");

        var helmetIndexes = helmets.ToDictionary(kv => kv.Key, kv => kv.Value.Index, StringComparer.Ordinal);

        var designNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reference is not null)
            foreach (var (meta, entry) in IndexKits(reference, "helmets", "Helmet", "_Helmet_instancecount_"))
                if (entry.Id is not null) designNames.TryAdd(entry.Id, meta);

        return new DriverCatalogue(drivers, helmetIndexes, suits, gloves, BuildHomeHelmets(helmetIndexes))
        {
            HelmetIds = helmets
                .Where(kv => kv.Value.Id is not null)
                .ToDictionary(kv => kv.Value.Index, kv => kv.Value.Id!),
            DesignNames = designNames,
            GenericHelmets = helmets
                .Where(kv => GenericHelmet.IsMatch(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.Index)
                .ToList(),
        };
    }

    private static List<DriverEntry> ReadDrivers(GameDatabase db)
    {
        var count = db.Int("entrants.drivers._driver_instancecount_");
        var list = new List<DriverEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var id = db.Text(Season.DriverPath(i, "_metadata_"));
            if (id.Length == 0 || id == "default_driver") continue;
            list.Add(new DriverEntry(id, db.Text(Season.DriverPath(i, "car_number"), "255")));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    private static Dictionary<string, KitEntry> IndexKits(
        GameDatabase db, string directory, string className, string countField)
    {
        var count = db.Int(Season.KitCountPath(directory, countField));
        var map = new Dictionary<string, KitEntry>(count, StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var meta = db.Text(Season.KitPath(directory, className, i, "_metadata_"));
            if (meta.Length == 0) continue;
            var id = db.Find(Season.KitPath(directory, className, i, "id"));
            map[meta] = new KitEntry(i, id is null || id.Data.Length == 0 ? null : id.Text);
        }

        return map;
    }

    // ── Home helmets ────────────────────────────────────────────────────────

    /// <summary>Modern F1/F2 helmets: <c>2025-redbull_max_verstappen</c>, <c>2025-f2-dams_jak_crawford</c>.</summary>
    [GeneratedRegex(@"^(20\d\d)-([a-z0-9-]+?)_(.+)$")]
    private static partial Regex ModernHelmet { get; }

    /// <summary>Unbranded helmets: <c>tier01_livery76</c>.</summary>
    [GeneratedRegex(@"^tier\d+_livery\d+$")]
    private static partial Regex GenericHelmet { get; }

    /// <summary>Classic/legend helmets: <c>classic-button01_jenson_button</c>.</summary>
    [GeneratedRegex(@"^classic-[a-z0-9]+_(.+)$")]
    private static partial Regex ClassicHelmet { get; }

    /// <summary>
    /// Work out which helmet record belongs to each driver.
    ///
    /// A driver often has several — one per season, plus event one-offs and F2
    /// or classic entries — so they're ranked: this season first, then last
    /// season, then other seasons newest-first, then F2, then classic. Within a
    /// tier the shorter team token wins, which is what separates a base team
    /// (<c>redbull</c>) from an event livery (<c>redbulljapan</c>).
    /// </summary>
    private static Dictionary<string, int> BuildHomeHelmets(Dictionary<string, int> helmets)
    {
        // Lower sorts first on every component.
        var candidates = new Dictionary<string, List<(int Tier, int NegativeYear, int TokenLength, int Index)>>(
            StringComparer.Ordinal);

        foreach (var (meta, index) in helmets)
        {
            var modern = ModernHelmet.Match(meta);
            if (modern.Success)
            {
                var year = int.Parse(modern.Groups[1].Value);
                var team = modern.Groups[2].Value;
                var driver = modern.Groups[3].Value;

                var tier = team.Contains('-') ? 3      // f2-dams and friends
                    : year == 2026 ? 0
                    : year == 2025 ? 1
                    : 2;

                Add(driver, (tier, -year, team.Length, index));
                continue;
            }

            var classic = ClassicHelmet.Match(meta);
            if (classic.Success) Add(classic.Groups[1].Value, (4, 0, meta.Length, index));
        }

        return candidates.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(c => c.Tier)
                          .ThenBy(c => c.NegativeYear)
                          .ThenBy(c => c.TokenLength)
                          .ThenBy(c => c.Index)
                          .First().Index,
            StringComparer.Ordinal);

        void Add(string driver, (int, int, int, int) candidate)
        {
            if (!candidates.TryGetValue(driver, out var list))
                candidates[driver] = list = [];
            list.Add(candidate);
        }
    }
}
