using System.Text.RegularExpressions;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedTeamEditor.Core.Teams;

/// <summary>A team's three names, as they read now.</summary>
public sealed class TeamNames
{
    public required TeamDefinition Team { get; init; }
    public string Full { get; set; } = "";
    public string Short { get; set; } = "";
    public string SuperShort { get; set; } = "";
}

/// <summary>
/// Reading team names out of a language file, and turning edited ones back into
/// the set of key/value changes a save or a package applies.
/// </summary>
public static class TeamNameEditing
{
    /// <summary>
    /// Values can carry the game's inline formatting — <c>{o:...}</c> around a
    /// run, closed by <c>{/o}</c>. It is markup, not part of the name, so it is
    /// hidden while a field is not being edited.
    /// </summary>
    private static readonly Regex Formatting = new(@"\{o:[^}]*\}|\{/o\}", RegexOptions.Compiled);

    /// <summary>The name without its formatting markup.</summary>
    public static string StripFormatting(string value) => Formatting.Replace(value, "");

    /// <summary>
    /// Read a season's names from a loaded language file.
    ///
    /// A 2026 key is absent from a stock game, so its value comes back empty
    /// and the caller fills it from the bundled defaults.
    /// </summary>
    public static List<TeamNames> Read(LngFile lng, Season season = Season.Y2025)
    {
        var names = new List<TeamNames>();
        foreach (var team in TeamGrid.For(season))
        {
            names.Add(new TeamNames
            {
                Team = team,
                Full = lng.Entries.GetValueOrDefault(team.FullKey, ""),
                Short = lng.Entries.GetValueOrDefault(team.ShortKey, ""),
                SuperShort = lng.Entries.GetValueOrDefault(team.SuperShortKey, ""),
            });
        }
        return names;
    }

    /// <summary>
    /// The key/value changes for a set of edited names.
    ///
    /// Mostly one entry per field, but two teams need more. Konnersport and
    /// APXGP are the game's optional eleventh and twelfth teams, and each has
    /// menu text naming them — so renaming the team has to rename its menu
    /// entry too, or the menu still offers the old name. Their "11th Team
    /// Enabled" strings are set only once a name actually differs from the
    /// stock one, matching what the launcher writes.
    ///
    /// An empty field is skipped rather than written, so a blank box leaves the
    /// game's own text in place instead of erasing it.
    /// </summary>
    public static Dictionary<string, string> BuildEdits(IEnumerable<TeamNames> teams)
    {
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var t in teams)
        {
            if (!string.IsNullOrEmpty(t.Full)) edits[t.Team.FullKey] = t.Full;
            if (!string.IsNullOrEmpty(t.Short)) edits[t.Team.ShortKey] = t.Short;
            if (!string.IsNullOrEmpty(t.SuperShort)) edits[t.Team.SuperShortKey] = t.SuperShort;

            // Only the 2025 grid has these two, and only their 2025 records
            // carry the menu strings — a 2026 row has neither.
            switch (t.Team.Id)
            {
                case "zen" when t.Team.DatabaseId == "zenith_25":
                    // A second short key, spelled the way the other teams' are.
                    if (!string.IsNullOrEmpty(t.Short)) edits["lng_team_zenith_title_short"] = t.Short;

                    var apxFull = string.IsNullOrEmpty(t.Full) ? "APXGP" : t.Full;
                    edits["lng_team_icons_menu_zenith"] = $"Add {apxFull}";
                    if (t.Full != "APXGP" || t.Short != "APXGP")
                        edits["lng_team_icons_zenith"] = "11th Team Enabled";
                    break;

                case "kon" when t.Team.DatabaseId == "konnersport":
                    var konShort = string.IsNullOrEmpty(t.Short) ? "Konnersport" : t.Short;
                    edits["lng_team_icons_menu_konnersport"] = $"Add {konShort}";
                    if (t.Full != "Konnersport Butler Global Racing Team" || t.Short != "Konnersport")
                        edits["lng_team_icons_konnersport"] = "11th Team Enabled";
                    break;
            }
        }

        return edits;
    }
}
