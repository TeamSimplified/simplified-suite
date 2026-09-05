using SimplifiedDriverSwapper.Core.Database;

namespace SimplifiedDriverSwapper.Core.Drivers;

/// <summary>
/// The identity fields on a driver record — the name shown on the tower, the
/// face, the commentary audio.
///
/// The three name fields hold <i>language keys</i>, not names: the game resolves
/// them through its language files, so typing a literal name gives you a missing
/// string rather than that name.
/// </summary>
public static class DriverAttributes
{
    public const string FirstName = "first_name";
    public const string LastName = "last_name";
    public const string FullName = "full_name";
    public const string Initials = "initials";
    public const string Nationality = "nationality";
    public const string Head = "head";
    public const string AudioName = "audio_name";

    public static readonly IReadOnlyList<string> Fields =
        [FirstName, LastName, FullName, Initials, Nationality, Head, AudioName];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [FirstName] = "First name (lng key)",
        [LastName] = "Last name (lng key)",
        [FullName] = "Full name (lng key)",
        [Initials] = "Initials (tower code)",
        [Nationality] = "Nationality",
        [Head] = "Head model (face)",
        [AudioName] = "Audio name (commentary)",
    };

    /// <summary>One driver's identity, as read from the database.</summary>
    public sealed class DriverRow
    {
        public required int Index { get; init; }
        public required string DriverId { get; init; }
        public required Dictionary<string, string> Values { get; init; }

        public string this[string field] => Values.GetValueOrDefault(field, "");
    }

    public static List<DriverRow> Read(GameDatabase db)
    {
        var count = db.Int("entrants.drivers._driver_instancecount_");
        var rows = new List<DriverRow>(count);

        for (var i = 0; i < count; i++)
        {
            var id = db.Text(Season.DriverPath(i, "_metadata_"));
            if (id.Length == 0) continue;

            var values = new Dictionary<string, string>(Fields.Count, StringComparer.Ordinal);
            foreach (var field in Fields) values[field] = db.Text(Season.DriverPath(i, field));

            rows.Add(new DriverRow { Index = i, DriverId = id, Values = values });
        }

        return rows;
    }

    /// <summary>
    /// The values already in use for each field, so the editor can offer real
    /// choices instead of asking someone to guess a language key.
    /// </summary>
    public static Dictionary<string, List<string>> ValuePools(IEnumerable<DriverRow> rows)
    {
        var pools = Fields.ToDictionary(f => f, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var row in rows)
            foreach (var field in Fields)
                if (row[field].Length > 0) pools[field].Add(row[field]);

        return pools.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Why a proposed edit can't be applied, or null when it's fine. Initials are
    /// optional; everything else would leave the game with nothing to show.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, string> proposed)
    {
        if (proposed.TryGetValue(Initials, out var initials) && initials.Length is > 4)
            return $"Initials should be 1–4 characters, got {initials.Length}.";

        foreach (var field in Fields)
        {
            if (field == Initials) continue;
            if (proposed.TryGetValue(field, out var value) && value.Length == 0)
                return $"{Labels[field]} can't be empty.";
        }

        return null;
    }
}
