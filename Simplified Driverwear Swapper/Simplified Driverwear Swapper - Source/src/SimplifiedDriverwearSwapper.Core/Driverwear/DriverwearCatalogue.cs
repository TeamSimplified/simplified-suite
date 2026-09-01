namespace SimplifiedDriverwearSwapper.Core.Driverwear;

/// <summary>
/// The wear sets in a game install: the team liveries under
/// <c>character_package/team_livery</c>, and the MyTeam suits under
/// <c>livery_package</c>.
///
/// This is plain directory listing rather than a Toolkit call: nothing here opens
/// an ERP, and <c>stk</c> has no folder-listing command. Anything that reads or
/// writes the archives themselves still goes through the Toolkit.
/// </summary>
public static class DriverwearCatalogue
{
    public enum WearSetKind
    {
        /// <summary>A real team, with drivers of its own.</summary>
        TeamLivery,

        /// <summary>A MyTeam suit. Same resource shape, different paths, no drivers.</summary>
        MyTeamSuit,
    }

    /// <summary>
    /// One set of driverwear.
    ///
    /// The two kinds hold the same resources under different roots, so the roots
    /// travel with the entry rather than being rebuilt from the name — that's
    /// what lets one pipeline handle both.
    /// </summary>
    /// <param name="Name">Token used throughout its identifiers, e.g. <c>2026-sauber</c> or <c>titlesponsor_livery01</c>.</param>
    /// <param name="ErpPath">The ERP holding it, or null when the folder has none.</param>
    /// <param name="IdfRoot">Identifier prefix its models live under, ending in <c>/idf/</c>.</param>
    /// <param name="TexturesRoot">Identifier prefix its textures live under, ending in <c>/textures/</c>.</param>
    public sealed record DriverwearEntry(
        string Name,
        WearSetKind Kind,
        string FolderPath,
        string? ErpPath,
        string IdfRoot,
        string TexturesRoot)
    {
        /// <summary>False when the folder is there but its ERP isn't — nothing to swap.</summary>
        public bool HasErp => ErpPath is not null;

        public string Label => Name;
        public override string ToString() => Name;
    }

    private const string LiverySubPath = "character_package/team_livery";
    private const string MyTeamSubPath = "livery_package";

    /// <summary>
    /// The team liveries, in folder order. These are the sets that have drivers.
    /// </summary>
    public static List<DriverwearEntry> Scan(string? gameDir)
    {
        var entries = new List<DriverwearEntry>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return entries;

        foreach (var liveryDir in FindUnder(gameDir, LiverySubPath))
        {
            IEnumerable<string> teamDirs;
            try { teamDirs = Directory.EnumerateDirectories(liveryDir); }
            catch { continue; }

            foreach (var teamDir in teamDirs)
            {
                var name = Path.GetFileName(teamDir);
                if (string.IsNullOrEmpty(name)) continue;

                // Every set ships as <folder>/<folder>.erp.
                var erp = Path.Combine(teamDir, name + ".erp");
                entries.Add(new DriverwearEntry(
                    name, WearSetKind.TeamLivery, teamDir,
                    File.Exists(erp) ? erp : null,
                    $"{LiverySubPath}/{name}/idf/",
                    $"{LiverySubPath}/{name}/textures/"));
            }
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The MyTeam suits, in folder order.
    ///
    /// They sit at <c>livery_package/&lt;category&gt;/&lt;item&gt;/&lt;item&gt;_suit.erp</c>. Only the
    /// suit is taken: it already carries the glove logos and glove textures, so
    /// it's the true counterpart of a team livery ERP. Items with a helmet but no
    /// suit — the replica helmets — have nothing to wear and are skipped.
    ///
    /// Textures live under a <i>separate</i> <c>livery_package_shared</c> root
    /// rather than beside the models, which is why the roots are recorded per
    /// entry instead of derived.
    /// </summary>
    public static List<DriverwearEntry> ScanMyTeamSuits(string? gameDir)
    {
        var entries = new List<DriverwearEntry>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return entries;

        foreach (var packageDir in FindUnder(gameDir, MyTeamSubPath))
        {
            IEnumerable<string> categoryDirs;
            try { categoryDirs = Directory.EnumerateDirectories(packageDir); }
            catch { continue; }

            foreach (var categoryDir in categoryDirs)
            {
                var category = Path.GetFileName(categoryDir);

                IEnumerable<string> itemDirs;
                try { itemDirs = Directory.EnumerateDirectories(categoryDir); }
                catch { continue; }

                foreach (var itemDir in itemDirs)
                {
                    var item = Path.GetFileName(itemDir);
                    if (string.IsNullOrEmpty(item)) continue;

                    var suit = Path.Combine(itemDir, item + "_suit.erp");
                    if (!File.Exists(suit)) continue;   // helmet-only items

                    entries.Add(new DriverwearEntry(
                        item, WearSetKind.MyTeamSuit, itemDir, suit,
                        $"{MyTeamSubPath}/{category}/{item}/idf/",
                        $"{MyTeamSubPath}_shared/{category}/{item}/textures/"));
                }
            }
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Folders matching a package-relative path, located under the asset group
    /// folders rather than by searching the install, which would mean walking
    /// every game file to find one directory.
    /// </summary>
    private static List<string> FindUnder(string gameDir, string subPath)
    {
        var found = new List<string>();

        IEnumerable<string> assetGroups;
        try { assetGroups = Directory.EnumerateDirectories(gameDir, "*asset_groups*"); }
        catch { return found; }

        foreach (var group in assetGroups)
        {
            var candidate = Path.Combine(group, subPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) found.Add(candidate);
        }

        return found;
    }
}
