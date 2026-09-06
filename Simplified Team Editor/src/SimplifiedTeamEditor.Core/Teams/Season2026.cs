using System.Text.Json;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedTeamEditor.Core.Teams;

/// <summary>
/// The 2026 grid, which the game carries alongside 2025 rather than instead
/// of it.
///
/// Editing it works differently, and the difference is the whole reason this
/// file exists. A 2025 name is changed by editing the value an existing key
/// already has. A 2026 name has no key of its own in a stock game — the teams
/// share the 2025 strings — so changing one means <em>adding</em> a key and
/// then pointing the database's name field at it. Two consequences follow:
/// the language files have to be rebuilt rather than patched, because a new
/// key changes the hash table; and the database has to be edited too, even for
/// a pure name change.
/// </summary>
public static class Season2026
{
    /// <summary>The database fields that name a team, and so can be repointed.</summary>
    public static readonly string[] NameFields =
    {
        "display_name", "display_name_short", "display_name_super_short",
    };

    private const string ReassignmentFile = "2026_team_names.json";
    private const string DefaultsFile = "2026_team_keys.lng";

    /// <summary>
    /// The eleven-team 2026 grid. Audi takes Sauber's record and Cadillac
    /// arrives on <c>gm_cadillac_26</c>; Konnersport and APXGP are not in this
    /// season at all.
    ///
    /// Accent colours are the 2025 team's where the outfit continues, and a
    /// neutral for the two that have no 2025 equivalent — the real colour
    /// replaces it as soon as the database is read.
    /// </summary>
    public static readonly IReadOnlyList<TeamDefinition> Teams = new[]
    {
        new TeamDefinition("mcl",  "mclaren_26",     "McLaren",      "#e18728",
            "lng_team_mcl_title_26", "lng_team_mcl_title_short_26", "lng_team_mcl_title_super_short_26"),
        new TeamDefinition("fer",  "ferrari_26",     "Ferrari",      "#9c1b1b",
            "lng_team_fer_title_26", "lng_team_fer_title_short_26", "lng_team_fer_title_super_short_26"),
        new TeamDefinition("rbr",  "redbull_26",     "Red Bull",     "#447baf",
            "lng_team_rbr_title_26", "lng_team_rbr_title_short_26", "lng_team_rbr_title_super_short_26"),
        new TeamDefinition("mer",  "mercedes_26",    "Mercedes",     "#39a3a2",
            "lng_team_mer_title_26", "lng_team_mer_title_short_26", "lng_team_mer_title_super_short_26"),
        new TeamDefinition("ast",  "forceindia_26",  "Aston Martin", "#2d7767",
            "lng_team_ast_title_26", "lng_team_ast_title_short_26", "lng_team_ast_title_super_short_26"),
        new TeamDefinition("alp",  "renault_26",     "Alpine",       "#345c94",
            "lng_team_alp_title_26", "lng_team_alp_title_short_26", "lng_team_alp_title_super_short_26"),
        new TeamDefinition("haas", "haas_26",        "Haas",         "#555555",
            "lng_team_haas_title_26", "lng_team_haas_title_short_26", "lng_team_haas_title_super_short_26"),
        // Racing Bulls keeps the Toro Rosso record, and takes its own key prefix.
        new TeamDefinition("rac",  "tororosso_26",   "Racing Bulls", "#3c519b",
            "lng_team_rac_title_26", "lng_team_rac_title_short_26", "lng_team_rac_title_super_short_26"),
        new TeamDefinition("wil",  "williams_26",    "Williams",     "#5190c1",
            "lng_team_wil_title_26", "lng_team_wil_title_short_26", "lng_team_wil_title_super_short_26"),
        // Audi on Sauber's record.
        new TeamDefinition("audi", "sauber_26",      "Audi",         "#4e964e",
            "lng_team_audi_title_26", "lng_team_audi_title_short_26", "lng_team_audi_title_super_short_26"),
        new TeamDefinition("cad",  "gm_cadillac_26", "Cadillac",     "#8a8f98",
            "lng_team_cad_title_26", "lng_team_cad_title_short_26", "lng_team_cad_title_super_short_26"),
    };

    /// <summary>
    /// The stock value of every 2026 name key, from the bundled defaults.
    ///
    /// These are what a 2026 name reads as before anyone edits it, and what
    /// gets seeded into the language files so the keys exist at all.
    /// </summary>
    public static Dictionary<string, string> LoadDefaults(string assetsDir)
    {
        var path = Path.Combine(assetsDir, DefaultsFile);
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);

        return new Dictionary<string, string>(LngFile.Load(path).Entries, StringComparer.Ordinal);
    }

    /// <summary>
    /// Which database name fields have to be repointed, per team.
    ///
    /// Only the fields a 2026 team shares with its 2025 self are listed — a
    /// field that already has its own key needs no repointing. Shape is
    /// <c>{database team id: {field: new key}}</c>.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> LoadReassignment(string assetsDir)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        var path = Path.Combine(assetsDir, ReassignmentFile);
        if (!File.Exists(path)) return result;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("changes", out var changes)) return result;

        foreach (var team in changes.EnumerateObject())
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in team.Value.EnumerateObject())
            {
                if (!NameFields.Contains(field.Name)) continue;
                var key = field.Value.GetString();
                if (!string.IsNullOrWhiteSpace(key)) fields[field.Name] = key;
            }
            if (fields.Count > 0) result[team.Name] = fields;
        }

        return result;
    }

    /// <summary>
    /// Point each 2026 team's shared name fields at its own keys, in place.
    /// Returns how many fields were repointed.
    ///
    /// The field holds a language key as text, so repointing is just writing a
    /// different key into it.
    /// </summary>
    public static int ApplyReassignment(
        GameDatabase db, IReadOnlyDictionary<string, Dictionary<string, string>> reassignment)
    {
        if (reassignment.Count == 0) return 0;

        var applied = 0;
        foreach (var (teamId, fields) in reassignment)
        {
            var index = TeamColours.FindIndex(db, teamId);
            if (index is null) continue;

            foreach (var (field, key) in fields)
            {
                var record = db.Find($"entrants.teams.team_{index.Value}.{field}");
                if (record is null) continue;
                record.Data = System.Text.Encoding.UTF8.GetBytes(key);
                applied++;
            }
        }
        return applied;
    }
}
