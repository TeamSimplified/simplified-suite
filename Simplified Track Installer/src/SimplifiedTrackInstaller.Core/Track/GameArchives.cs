using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;

namespace SimplifiedTrackInstaller.Core.Track;

/// <summary>
/// Finds the game's own copy of a resource.
///
/// The menu databases and texture arrays a track is added to live in the UI
/// archives, several of them carrying the same resource. Which one holds what
/// is read from the resource table alone, and an archive is only loaded whole
/// when something is wanted from it, then kept, since the next lookup usually
/// wants the same one.
/// </summary>
internal sealed class GameArchives(string gameDir)
{
    public const string UiFolder = "2025_asset_groups/ui_package";

    /// <summary>
    /// Where the UI databases and texture arrays are looked for, in order. The
    /// first three hold every array a track is added to; the rest carry copies
    /// of the same ones and are only there in case a game update moves them.
    /// </summary>
    public static readonly string[] UiArchives =
    [
        $"{UiFolder}/databases.erp",
        $"{UiFolder}/persistent.erp",
        $"{UiFolder}/common_front_end.erp",
        $"{UiFolder}/flow_career_season_setup.erp",
        $"{UiFolder}/flow_frontend.erp",
        $"{UiFolder}/flow_race.erp",
        $"{UiFolder}/flow_f1_world.erp",
        $"{UiFolder}/flow_career_hub.erp",
        $"{UiFolder}/flow_car_monitor.erp",
        $"{UiFolder}/screens/challenge_career_episode.erp",
    ];

    private readonly Dictionary<string, HashSet<string>?> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ErpFile> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public string FullPath(string relative) =>
        Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The resource from the first of <paramref name="archives"/> that has it, or null.</summary>
    public ErpResource? Find(string identifier, IEnumerable<string> archives)
    {
        foreach (var relative in archives)
        {
            if (Index(relative) is not { } ids || !ids.Contains(identifier)) continue;
            return Load(relative).Resources.Find(r => r.Identifier == identifier);
        }

        return null;
    }

    /// <summary>Let go of every archive loaded so far.</summary>
    public void Release() => _loaded.Clear();

    private HashSet<string>? Index(string relative)
    {
        if (_indexes.TryGetValue(relative, out var known)) return known;

        var index = ErpIndex.TryRead(FullPath(relative));
        return _indexes[relative] = index?.Resources.Select(r => r.Identifier).ToHashSet(StringComparer.Ordinal);
    }

    private ErpFile Load(string relative)
    {
        if (!_loaded.TryGetValue(relative, out var erp))
            _loaded[relative] = erp = ErpFileIO.Load(FullPath(relative));
        return erp;
    }
}
