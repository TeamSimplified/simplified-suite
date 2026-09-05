using System.Globalization;
using SimplifiedTeamEditor.Core.GameDatabase;

namespace SimplifiedTeamEditor.Core.Teams;

/// <summary>The four colours a team has in the database.</summary>
public enum ColourField
{
    /// <summary>Accent in the UI: standings, team-pick screens.</summary>
    UiTeamColour,
    Primary,
    Secondary,
    Tertiary,
}

/// <summary>One team's colours, and where they came from.</summary>
public sealed class TeamColourSet
{
    public required string TeamId { get; init; }

    /// <summary>The team's index in the database, which is how its records are addressed.</summary>
    public required int Index { get; init; }

    /// <summary>Field to <c>#RRGGBB</c>. A field the database doesn't carry is absent.</summary>
    public Dictionary<ColourField, string> Colours { get; } = new();
}

/// <summary>
/// Reading and writing team colours in <c>gamedatabases.bin</c>.
/// </summary>
public static class TeamColours
{
    public static readonly IReadOnlyList<ColourField> Fields = new[]
    {
        ColourField.UiTeamColour, ColourField.Primary, ColourField.Secondary, ColourField.Tertiary,
    };

    public static string FieldName(ColourField field) => field switch
    {
        ColourField.UiTeamColour => "ui_team_colour",
        ColourField.Primary => "primary_colour",
        ColourField.Secondary => "secondary_colour",
        ColourField.Tertiary => "tertiary_colour",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>How a field is captioned in the editor.</summary>
    public static string FieldLabel(ColourField field) => field switch
    {
        ColourField.UiTeamColour => "UI accent",
        ColourField.Primary => "Primary",
        ColourField.Secondary => "Secondary",
        ColourField.Tertiary => "Tertiary",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string RecordPath(int index, string field) => $"entrants.teams.team_{index}.{field}";

    /// <summary>
    /// How many team slots to look through.
    ///
    /// Indices are not necessarily contiguous — a patch can leave a hole — so a
    /// caller walks the whole range and skips the gaps rather than stopping at
    /// the first miss, which would silently drop every team past it.
    /// </summary>
    private static int TeamSlotCount(GameDatabaseFile db, int cap = 400)
    {
        var record = db.Find("entrants.teams._team_instancecount_");
        if (record is { Data.Length: > 0 }
            && int.TryParse(record.Text.Trim(), out var n) && n > 0)
            return n;
        return cap;
    }

    /// <summary>The database index for a team id, or null if it isn't there.</summary>
    public static int? FindIndex(GameDatabaseFile db, string databaseTeamId)
    {
        var slots = TeamSlotCount(db);
        for (var i = 0; i < slots; i++)
        {
            var meta = db.Find(RecordPath(i, "_metadata_"));
            if (meta is null || meta.Data.Length == 0) continue;   // a gap, not the end
            if (meta.Text == databaseTeamId) return i;
        }
        return null;
    }

    /// <summary>A season's colours, for the teams the database carries.</summary>
    public static List<TeamColourSet> ReadAll(GameDatabaseFile db, Season season = Season.Y2025)
    {
        var sets = new List<TeamColourSet>();

        foreach (var team in TeamGrid.For(season))
        {
            var index = FindIndex(db, team.DatabaseId);
            if (index is null) continue;

            var set = new TeamColourSet { TeamId = team.Id, Index = index.Value };
            foreach (var field in Fields)
            {
                var record = db.Find(RecordPath(index.Value, FieldName(field)));
                if (record is { Data.Length: > 0 } && TryDecode(record.Text, out var hex))
                    set.Colours[field] = hex;
            }
            sets.Add(set);
        }

        return sets;
    }

    /// <summary>
    /// Set one colour. Returns false when the database has no such field for
    /// that team, which is not an error — not every team carries all four.
    /// </summary>
    public static bool Write(GameDatabaseFile db, int index, ColourField field, string hex)
    {
        var record = db.Find(RecordPath(index, FieldName(field)));
        if (record is null) return false;

        record.Data = System.Text.Encoding.UTF8.GetBytes(Encode(hex));
        return true;
    }

    // ─────────────────────── Colour as the game stores it ──────────────────

    /// <summary>
    /// Colours are held as the decimal text of a signed 32-bit ARGB value, so
    /// <c>#E18728</c> reads as "-1996504". Text, not bytes: the payload really
    /// is the digits.
    /// </summary>
    public static bool TryDecode(string payload, out string hex)
    {
        hex = "";
        if (!int.TryParse(payload.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return false;

        var unsigned = unchecked((uint)value);
        hex = $"#{(unsigned >> 16) & 0xFF:X2}{(unsigned >> 8) & 0xFF:X2}{unsigned & 0xFF:X2}";
        return true;
    }

    /// <summary>
    /// The reverse. Alpha is forced opaque — the game expects it, and the
    /// editor has no way to set it.
    /// </summary>
    public static string Encode(string hex)
    {
        var h = hex.TrimStart('#').Trim();
        if (h.Length != 6 || !h.All(Uri.IsHexDigit))
            throw new ArgumentException($"Expected #RRGGBB, got '{hex}'.", nameof(hex));

        var r = Convert.ToUInt32(h[..2], 16);
        var g = Convert.ToUInt32(h.Substring(2, 2), 16);
        var b = Convert.ToUInt32(h.Substring(4, 2), 16);
        var unsigned = (0xFFu << 24) | (r << 16) | (g << 8) | b;

        return unchecked((int)unsigned).ToString(CultureInfo.InvariantCulture);
    }
}
