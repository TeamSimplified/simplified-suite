using EgoEngineLibrary.Data.GameDatabases;

// The database stores no paths — only hashes of them — so exploring it means
// guessing a path and seeing whether it resolves. This prints what's actually
// there for a given prefix, which is how the grid tables were worked out.
//
//   DbProbe <gamedatabases.bin> lists
//   DbProbe <gamedatabases.bin> list <n>

var db = GameDatabase.Load(args[0]);
var mode = args.Length > 1 ? args[1] : "lists";

if (mode == "lists")
{
    var count = db.Int("entrants.entrant_lists._entrant_list_instancecount_");
    Console.WriteLine($"entrant lists: {count}");
    for (var i = 0; i < Math.Max(count, 64); i++)
    {
        var meta = db.Text($"entrants.entrant_lists.entrant_list_{i}._metadata_");
        var first = db.Find($"entrants.entrant_lists.entrant_list_{i}.entrant_0.driver");
        if (meta.Length == 0 && first is null) continue;

        var seats = 0;
        while (db.Find($"entrants.entrant_lists.entrant_list_{i}.entrant_{seats}.driver") is not null) seats++;
        Console.WriteLine($"  list {i,3}  {meta,-28} seats={seats}");
    }
    return 0;
}

if (mode == "list")
{
    var n = int.Parse(args[2]);
    Console.WriteLine($"entrant_list_{n}  ({db.Text($"entrants.entrant_lists.entrant_list_{n}._metadata_")})");

    // Which fields an entrant record actually has, found by trying names.
    string[] candidates =
    [
        "_metadata_", "driver", "car_number", "team", "entrant_team", "vehicle",
        "livery", "name", "short_name", "nationality", "is_player", "position",
    ];

    var present = new List<string>();
    foreach (var f in candidates)
        if (db.Find($"entrants.entrant_lists.entrant_list_{n}.entrant_0.{f}") is not null) present.Add(f);
    Console.WriteLine($"fields present: {string.Join(", ", present)}");
    Console.WriteLine();

    for (var slot = 0; ; slot++)
    {
        var prefix = $"entrants.entrant_lists.entrant_list_{n}.entrant_{slot}";
        if (db.Find($"{prefix}.driver") is null) break;
        var values = present.Select(f => $"{f}={db.Text($"{prefix}.{f}", "")}");
        Console.WriteLine($"  {slot,2}  {string.Join("  ", values)}");
    }
    return 0;
}

if (mode == "kit")
{
    // Kit records for a season, grouped by team token in record-index order.
    // If that order matches the seat order, the default lineup can be derived
    // from the database instead of hardcoded.
    var year = args[2];
    var count = db.Int("customisation.custom_avatar.suits._Suit_instancecount_");
    var byTeam = new Dictionary<string, List<(int Index, string Driver)>>(StringComparer.Ordinal);

    for (var i = 0; i < count; i++)
    {
        var meta = db.Text($"customisation.custom_avatar.suits.Suit_{i}._metadata_");
        if (!meta.StartsWith(year + "-", StringComparison.Ordinal)) continue;
        var rest = meta[(year.Length + 1)..];
        var us = rest.IndexOf('_');
        if (us < 0) continue;                       // "2026-mclaren" = the generic team kit
        var team = rest[..us];
        var driver = rest[(us + 1)..];
        if (!byTeam.TryGetValue(team, out var list)) byTeam[team] = list = new();
        list.Add((i, driver));
    }

    foreach (var (team, list) in byTeam.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {team,-14} {string.Join("  ->  ", list.OrderBy(e => e.Index).Select(e => $"{e.Driver} (Suit_{e.Index})"))}");
    return 0;
}

if (mode == "populated")
{
    // Which entrant lists actually name their drivers. A list that does is a
    // source of truth for seat order; f1_2025 and f1_2026 leave it to the game.
    var count = db.Int("entrants.entrant_lists._entrant_list_instancecount_");
    for (var i = 0; i < count; i++)
    {
        var meta = db.Text($"entrants.entrant_lists.entrant_list_{i}._metadata_");
        int seats = 0, filled = 0;
        while (db.Find($"entrants.entrant_lists.entrant_list_{i}.entrant_{seats}.driver") is not null)
        {
            if (db.Text($"entrants.entrant_lists.entrant_list_{i}.entrant_{seats}.driver").Length > 0) filled++;
            seats++;
        }
        if (filled > 0) Console.WriteLine($"  list {i,3}  {meta,-34} {filled}/{seats} named");
    }
    return 0;
}

if (mode == "find")
{
    // Kit metadata containing a substring, across all three kinds.
    var needle = args[2];
    foreach (var (dir, cls, countField) in new[]
             {
                 ("helmets", "Helmet", "_Helmet_instancecount_"),
                 ("suits", "Suit", "_Suit_instancecount_"),
                 ("gloves", "Gloves", "_Gloves_instancecount_"),
             })
    {
        var n = db.Int($"customisation.custom_avatar.{dir}.{countField}");
        for (var i = 0; i < n; i++)
        {
            var meta = db.Text($"customisation.custom_avatar.{dir}.{cls}_{i}._metadata_");
            if (meta.Contains(needle, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  {dir,-8} {cls}_{i,-4}  {meta}");
        }
    }
    return 0;
}

if (mode == "fields")
{
    // Which fields a kit record has, found by trying names.
    var dir = args[2]; var cls = args[3]; var idx = args[4];
    string[] candidates =
    [
        "_metadata_", "id", "name", "asset", "texture", "livery", "type",
        "colour", "color", "design", "team", "driver", "index", "tier",
    ];
    foreach (var f in candidates)
    {
        var rec = db.Find($"customisation.custom_avatar.{dir}.{cls}_{idx}.{f}");
        if (rec is not null) Console.WriteLine($"  {f,-12} = {rec.Text}");
    }
    return 0;
}

Console.Error.WriteLine("usage: DbProbe <db> lists | list <n> | kit <year> | populated | find <text> | fields <dir> <cls> <i>");
return 2;
