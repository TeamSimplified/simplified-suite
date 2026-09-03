using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedHelmetSwapper.Core.Helmet;

/// <summary>
/// The helmets in a game install, and what each one is made of.
///
/// Every driver keeps their helmet in <c>&lt;driver&gt;_helmet.erp</c> beside their
/// own ERP. Listing them is a directory walk — reading one is not, so the scan
/// only names them and <see cref="Read"/> opens the two a swap actually needs.
/// </summary>
public static class HelmetCatalogue
{
    private const string DriversSubPath = "character_package/drivers";

    /// <summary>One driver's helmet, as far as the file system can tell.</summary>
    /// <param name="Folder">The driver folder, e.g. <c>2026-ferrari_charles_leclerc</c>.</param>
    /// <param name="Driver">The driver token used throughout their resources, e.g. <c>charles_leclerc</c>.</param>
    /// <param name="Team">The folder's team half, e.g. <c>2026-ferrari</c>.</param>
    public sealed record Entry(string Folder, string Driver, string Team, string Gender, string ErpPath)
    {
        /// <summary>Their own resources all live under this, whatever their team.</summary>
        public string Root => $"eaid://character_package/drivers/{Gender}/{Folder}/";

        /// <summary>How the driver reads in a list — "Charles Leclerc — 2026 Audi".</summary>
        public string Label => $"{Pretty(Driver)} — {TeamLabel(Team)}";

        public override string ToString() => Label;
    }

    /// <summary>What a helmet ERP turned out to hold. Read once, per swap.</summary>
    /// <param name="Shell">
    /// The generic shell the helmet is built on, e.g. <c>helmet_bell_hp7</c>.
    /// Five exist across the game and a driver wears exactly one.
    /// </param>
    /// <param name="Families">
    /// Which of the helmet's texture families this driver has of their own —
    /// <c>helmet</c>, <c>helmet_wing</c>, <c>visor</c>, <c>visor_attachments</c>.
    /// Not every driver has all four, and a material exists exactly where its
    /// textures do.
    /// </param>
    public sealed record Contents(string Shell, IReadOnlyList<string> Families, int ResourceCount);

    /// <summary>The four families that make up a helmet, in the order they read.</summary>
    public static readonly string[] HelmetFamilies = ["helmet", "helmet_wing", "visor", "visor_attachments"];

    /// <summary>
    /// Every driver helmet under the install, both genders, sorted by team then
    /// driver so team-mates sit together.
    /// </summary>
    public static IReadOnlyList<Entry> Scan(string? gameDir)
    {
        var found = new List<Entry>();
        foreach (var root in DriverRoots(gameDir))
            foreach (var gender in new[] { "male", "female" })
            {
                var genderDir = Path.Combine(root, gender);
                if (!Directory.Exists(genderDir)) continue;

                foreach (var dir in Directory.EnumerateDirectories(genderDir))
                {
                    var folder = Path.GetFileName(dir);
                    foreach (var erp in Directory.EnumerateFiles(dir, "*_helmet.erp"))
                    {
                        var driver = Path.GetFileNameWithoutExtension(erp);
                        driver = driver[..^"_helmet".Length];

                        // The folder is "<team>_<driver>", and the driver half is
                        // the one the file name already gave us — so the rest is
                        // the team, however many underscores it holds.
                        var team = folder.EndsWith("_" + driver, StringComparison.OrdinalIgnoreCase)
                            ? folder[..^(driver.Length + 1)]
                            : folder;

                        if (found.Any(e => e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                            continue;   // an earlier root already had this driver

                        found.Add(new Entry(folder, driver, team, gender, erp));
                    }
                }
            }

        return found
            .OrderBy(e => e.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Driver, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Open a helmet and say what it is built from.
    ///
    /// A driver whose helmet holds nothing under their own folder isn't one this
    /// app can swap — the classic-custom entries keep theirs under
    /// <c>team_livery/</c> instead, which is a different shape of thing.
    /// </summary>
    public static Contents? Read(Entry entry)
    {
        ErpFile erp;
        try { erp = ErpFileIO.Load(entry.ErpPath); }
        catch { return null; }

        var ids = erp.Resources.Select(r => r.Identifier).ToList();

        var shell = ids
            .Select(i => Regex.Match(i, @"kitpart_assets/generic/helmets/([^/]+)/"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct().FirstOrDefault();
        if (shell is null) return null;

        // A family counts as the driver's own only when it sits under their
        // folder: the shared ones live under a placeholder root the game fills
        // in at load time, and those are nobody's to swap.
        var families = HelmetFamilies
            .Where(f => ids.Any(i =>
                i.StartsWith(entry.Root, StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(i, $@"/{Regex.Escape(entry.Driver)}_{Regex.Escape(f)}_[dns]\.tif$")))
            .ToList();

        return new Contents(shell, families, erp.Resources.Count);
    }

    /// <summary>Every <c>drivers/</c> folder in the install, asset groups first.</summary>
    private static List<string> DriverRoots(string? gameDir)
    {
        var roots = new List<string>();
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return roots;

        var sub = DriversSubPath.Replace('/', Path.DirectorySeparatorChar);

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
    /// The 2026 folders whose name isn't the team's.
    ///
    /// The game's folders are historical rather than current — Audi races out
    /// of <c>sauber</c>, Aston Martin out of <c>force_india</c> — so a list
    /// built straight from them names teams nobody is looking for. Only the
    /// 2026 folders are mapped: the same tokens in an earlier season really
    /// were those teams, and renaming those would be the same mistake.
    /// </summary>
    private static readonly Dictionary<string, string> TeamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sauber"] = "Audi",
        ["forceindia"] = "Aston Martin",
        ["tororosso"] = "Racing Bulls",
        ["renault"] = "Alpine",
        ["redbull"] = "Red Bull",
        ["gm_cadillac"] = "Cadillac",
    };

    /// <summary>The team half of a folder, as someone following the sport would say it.</summary>
    private static string TeamLabel(string team)
    {
        var dash = team.IndexOf('-');
        if (dash > 0 && team[..dash] == "2026" && TeamNames.TryGetValue(team[(dash + 1)..], out var current))
            return $"2026 {current}";
        return Pretty(team);
    }

    /// <summary>"2026-ferrari" reads as "2026 Ferrari", "charles_leclerc" as "Charles Leclerc".</summary>
    private static string Pretty(string token) => string.Join(' ', token
        .Replace('_', ' ').Replace('-', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.Length > 0 && char.IsLetter(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
}
