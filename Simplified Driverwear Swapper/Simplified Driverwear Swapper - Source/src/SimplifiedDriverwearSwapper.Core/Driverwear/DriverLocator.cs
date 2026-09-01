namespace SimplifiedDriverwearSwapper.Core.Driverwear;

/// <summary>
/// Finds the drivers belonging to a livery set.
///
/// Driver folders sit under <c>character_package/drivers/&lt;gender&gt;/</c> and are
/// named <c>&lt;livery set&gt;_&lt;driver&gt;</c>, e.g.
/// <c>2025-forceindia_fernando_alonso</c>.
/// </summary>
public static class DriverLocator
{
    /// <summary>One driver's folder.</summary>
    /// <param name="LiverySet">The set they belong to, e.g. <c>2025-forceindia</c>.</param>
    /// <param name="DriverName">Their own name, e.g. <c>fernando_alonso</c>.</param>
    /// <param name="Gender">The subfolder they live in — <c>male</c> or <c>female</c>.</param>
    /// <param name="FolderPath">The folder itself.</param>
    /// <param name="ErpPath">
    /// Their <c>&lt;driver&gt;.erp</c>, or null if absent. The
    /// <c>&lt;driver&gt;_helmet.erp</c> sitting beside it is deliberately ignored —
    /// a helmet isn't driverwear.
    /// </param>
    public sealed record DriverFolder(
        string LiverySet,
        string DriverName,
        string Gender,
        string FolderPath,
        string? ErpPath)
    {
        /// <summary>Folder name as it appears on disk.</summary>
        public string FolderName => Path.GetFileName(FolderPath);

        /// <summary>Driver name with underscores turned into spaces, for display.</summary>
        public string DisplayName => DriverName.Replace('_', ' ');
    }

    /// <summary>The gender subfolders searched, in the order they're reported.</summary>
    private static readonly string[] GenderFolders = { "male", "female" };

    private const string DriversSubPath = "character_package/drivers";

    /// <summary>
    /// Every driver in a livery set, across both gender folders — a set can span
    /// them, as konnersport does.
    ///
    /// The match requires the underscore after the set name. Without it
    /// <c>2025-haas</c> would also claim <c>2025-haasmontreal</c> and
    /// <c>2025-haastexas</c>, which are separate sets with their own drivers.
    /// </summary>
    public static List<DriverFolder> FindDrivers(string? gameDir, string liverySet)
    {
        var drivers = new List<DriverFolder>();
        if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(liverySet)) return drivers;

        var prefix = liverySet + "_";

        foreach (var driversDir in FindDriversDirs(gameDir))
        {
            foreach (var gender in GenderFolders)
            {
                var genderDir = Path.Combine(driversDir, gender);
                if (!Directory.Exists(genderDir)) continue;

                IEnumerable<string> folders;
                try { folders = Directory.EnumerateDirectories(genderDir, prefix + "*"); }
                catch { continue; }

                foreach (var folder in folders)
                {
                    var name = Path.GetFileName(folder);

                    // EnumerateDirectories' pattern is case-insensitive and can
                    // match more loosely than expected, so confirm the prefix.
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var driverName = name[prefix.Length..];
                    if (driverName.Length == 0) continue;

                    var erp = Path.Combine(folder, driverName + ".erp");

                    drivers.Add(new DriverFolder(
                        liverySet, driverName, gender, folder,
                        File.Exists(erp) ? erp : null));
                }
            }
        }

        return drivers
            .OrderBy(d => Array.IndexOf(GenderFolders, d.Gender))
            .ThenBy(d => d.DriverName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The <c>drivers</c> folders in an install, found under the asset group
    /// folders rather than by searching the whole install.
    /// </summary>
    private static List<string> FindDriversDirs(string gameDir)
    {
        var found = new List<string>();
        if (!Directory.Exists(gameDir)) return found;

        IEnumerable<string> assetGroups;
        try { assetGroups = Directory.EnumerateDirectories(gameDir, "*asset_groups*"); }
        catch { return found; }

        foreach (var group in assetGroups)
        {
            var drivers = Path.Combine(group, DriversSubPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(drivers)) found.Add(drivers);
        }

        return found;
    }
}
