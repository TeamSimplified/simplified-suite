namespace SimplifiedSwapper.Core.Project;

/// <summary>
/// Which team's ERP a template is for.
///
/// A template doesn't say whose it is in so many words, but its shader package
/// does: every team in the 2026 pack is assigned a different one, and it travels
/// in the archive's ZIP comment as
/// <c>{"erp_file_string":"object_flag.nefx2.sm51"}</c>. That is the identifier
/// the game itself keys on, so it is the one to match — the folder names and the
/// display names in a template are only labels, and a renamed template would
/// still be the same team's.
///
/// The pairing is hardcoded because there is nothing to derive it from: the
/// shader names carry no meaning (Ferrari is <c>object_flag</c>, Williams
/// <c>object_roof</c>), and the game's own team folders are historical rather
/// than current — Alpine is <c>lotus</c>, Aston Martin is <c>force_india</c>,
/// Audi is <c>sauber</c> and Racing Bulls is <c>toro_rosso</c>.
/// </summary>
public static class TeamErps
{
    /// <summary>One team ERP that could be the slot: what to call it, and where it is.</summary>
    public sealed record Candidate(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    /// <summary>A team, and everything about it that differs from its name.</summary>
    /// <param name="Folders">
    /// Folders under <c>teams/</c> holding the team's chassis ERP. All but My
    /// Team have exactly one — My Team ships a slot per engine supplier, and
    /// which of them a swap takes over is a real choice, not something a
    /// template can answer.
    /// </param>
    /// <param name="DriverwearSet">
    /// The set under <c>character_package/team_livery</c> this team's drivers
    /// wear, or null when its wear is a MyTeam suit instead. A third set of
    /// names again: Alpine's wear is <c>2026-renault</c>, Aston Martin's is
    /// <c>2026-forceindia</c>.
    /// </param>
    /// <param name="Code">
    /// The team's short code. Nothing reads it yet: the app used to build a
    /// replacement name out of it, and that is back in the user's hands. Kept
    /// because the codes were given rather than derived, and the work on
    /// renaming at any length will want them.
    /// </param>
    /// <param name="ChassisSwapWorks">
    /// False for My Team. A swapped chassis can be built for its slots and the
    /// game will not load it — so the app doesn't offer to build one. Kept
    /// apart from <paramref name="OnTheGrid"/> deliberately: that one is about
    /// which cars are on the grid, this is about what the game accepts, and
    /// they coincide today by accident rather than for a reason.
    /// </param>
    /// <param name="OnTheGrid">
    /// False for My Team, which is the player's own entry rather than one of the
    /// eleven. Its slots are still somewhere a swap can go — that is what a My
    /// Team template is for — but they are not cars anyone picks to drive.
    /// </param>
    private sealed record Team(string Name, string[] Folders, string? DriverwearSet,
                               string Code, bool OnTheGrid = true, bool ChassisSwapWorks = true);

    private const string TeamsSubPath = "f1_2026_vehicle_package/teams";

    private static readonly Dictionary<string, Team> ByShaderPackage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["object_alpha.nefx2.sm51"]              = new("Alpine",        ["lotus"],       "2026-renault", "alp"),
        ["object_auto_illuminate.nefx2.sm51"]    = new("Aston Martin",  ["force_india"], "2026-forceindia", "ast"),
        ["object_car_monitor.nefx2.sm51"]        = new("Audi",          ["sauber"],      "2026-sauber", "audi"),
        ["object_driver_name_shader.nefx2.sm51"] = new("Cadillac",      ["gm_cadillac"], "2026-cadillac", "cad"),
        ["object_flag.nefx2.sm51"]               = new("Ferrari",       ["ferrari"],     "2026-ferrari", "fer"),
        ["object_glass.nefx2.sm51"]              = new("Haas",          ["haas"],        "2026-haas", "haas"),
        ["object_jumbotron.nefx2.sm51"]          = new("McLaren",       ["mclaren"],     "2026-mclaren", "mcl"),
        ["object_mask.nefx2.sm51"]               = new("Mercedes",      ["mercedes"],    "2026-mercedes", "mer"),
        ["object_opaque.nefx2.sm51"]             = new("Racing Bulls",  ["toro_rosso"],  "2026-tororosso", "rac"),
        ["object_overlay.nefx2.sm51"]            = new("Red Bull",      ["redbull"],     "2026-redbull", "rbr"),
        ["object_roof.nefx2.sm51"]               = new("Williams",      ["williams"],    "2026-williams", "wil"),

        ["object_my_team.nefx2.sm51"] = new("My Team",
        [
            "myteam_audi", "myteam_ferrari", "myteam_honda",
            "myteam_mercedes", "myteam_redbull_ford", "myteam_renault",
        ], DriverwearSet: null, Code: "myt", OnTheGrid: false, ChassisSwapWorks: false),
    };

    /// <summary>The team a shader package belongs to, or null for one we don't know.</summary>
    public static string? TeamNameOf(string? shaderPackage) =>
        shaderPackage is not null && ByShaderPackage.TryGetValue(shaderPackage, out var t) ? t.Name : null;

    /// <summary>
    /// The driverwear set this template's drivers wear, e.g.
    /// <c>2026-ferrari</c>. Null for My Team, whose wear is one of the player's
    /// own suits rather than a team set — see <see cref="WearsMyTeamSuits"/> —
    /// and null for a shader package we don't know.
    /// </summary>
    public static string? DriverwearSetOf(string? shaderPackage) =>
        shaderPackage is not null && ByShaderPackage.TryGetValue(shaderPackage, out var t)
            ? t.DriverwearSet : null;

    /// <summary>
    /// Whether a chassis swap into this template is worth offering.
    ///
    /// False for My Team: the swap builds, and the game doesn't load it. False
    /// too for a package we don't recognise, where there is no slot to swap
    /// into at all.
    /// </summary>
    public static bool CanSwapChassis(string? shaderPackage) =>
        shaderPackage is not null &&
        ByShaderPackage.TryGetValue(shaderPackage, out var team) &&
        team.ChassisSwapWorks;

    /// <summary>True when the template's wear comes from the player's MyTeam suits.</summary>
    public static bool WearsMyTeamSuits(string? shaderPackage) =>
        shaderPackage is not null && ByShaderPackage.TryGetValue(shaderPackage, out var t) &&
        t.DriverwearSet is null;

    /// <summary>
    /// The team ERPs a template could take over, in the order they should be
    /// offered. Empty when the shader package is unknown, the game folder isn't
    /// set, or none of the team's ERPs are actually installed.
    ///
    /// Only the canonical <c>&lt;team&gt;/wep/&lt;team&gt;.erp</c> counts. The
    /// same folders hold one-off 2025 liveries and whatever copies people have
    /// left lying about, and neither is the slot a 2026 template replaces.
    /// </summary>
    public static IReadOnlyList<Candidate> For(string? gameDir, string? shaderPackage)
    {
        if (shaderPackage is null || !ByShaderPackage.TryGetValue(shaderPackage, out var team))
            return [];

        var roots = TeamRoots(gameDir);
        var found = new List<Candidate>();

        foreach (var folder in team.Folders)
            foreach (var root in roots)
            {
                var path = Path.Combine(root, folder, "wep", folder + ".erp");
                if (!File.Exists(path)) continue;
                // One slot reads as the team; My Team's six need saying which
                // is which, and there the team is already on the title bar.
                found.Add(new Candidate(
                    team.Folders.Length == 1 ? team.Name : EngineLabel(folder), path));
                break;    // first root that has it wins
            }

        return found;
    }

    /// <summary>
    /// Every 2026 car in the install, by team, for picking one to swap in.
    ///
    /// The same ERPs <see cref="For"/> offers as slots — a car and the slot it
    /// sits in are the same file — but listed across all the teams rather than
    /// the one a template names, because which car you want to drive is the
    /// choice the template doesn't make.
    /// </summary>
    public static IReadOnlyList<Candidate> AllCars(string? gameDir)
    {
        var roots = TeamRoots(gameDir);
        var cars = new List<Candidate>();

        foreach (var team in ByShaderPackage.Values
                     .Where(t => t.OnTheGrid)
                     .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var folder in team.Folders)
                foreach (var root in roots)
                {
                    var path = Path.Combine(root, folder, "wep", folder + ".erp");
                    if (!File.Exists(path)) continue;

                    // One car each, named as the team — everything on the grid
                    // has exactly one folder, so the loop never adds twice.
                    cars.Add(new Candidate(team.Name, path));
                    break;
                }

        return cars;
    }

    /// <summary>
    /// Every <c>teams/</c> folder in the install, asset groups first.
    ///
    /// The pack sits under an asset group and again at the game root, and only
    /// the asset-group copy holds the ERPs — the root one is the empty shell the
    /// game overlays. Both are searched, in that order, rather than assuming
    /// which install this is.
    /// </summary>
    private static List<string> TeamRoots(string? gameDir)
    {
        var roots = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return roots;

        var sub = TeamsSubPath.Replace('/', Path.DirectorySeparatorChar);

        try
        {
            foreach (var group in Directory.EnumerateDirectories(gameDir, "*asset_groups*"))
            {
                var candidate = Path.Combine(group, sub);
                if (Directory.Exists(candidate)) roots.Add(candidate);
            }
        }
        catch { /* unreadable install — the root below may still work */ }

        var atRoot = Path.Combine(gameDir, sub);
        if (Directory.Exists(atRoot)) roots.Add(atRoot);

        return roots;
    }

    /// <summary>
    /// What to call a slot. Only My Team has more than one, and there the folder
    /// name is the engine, which is the whole of what tells them apart.
    /// </summary>
    private static string EngineLabel(string folder)
    {
        if (!folder.StartsWith("myteam_", StringComparison.OrdinalIgnoreCase))
            return folder;

        return folder["myteam_".Length..] switch
        {
            "audi"          => "Audi engine",
            "ferrari"       => "Ferrari engine",
            "honda"         => "Honda engine",
            "mercedes"      => "Mercedes engine",
            "redbull_ford"  => "Red Bull Ford engine",
            "renault"       => "Renault engine",
            var other       => other + " engine",
        };
    }
}
