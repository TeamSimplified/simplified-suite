using EgoEngineLibrary.Operations;

namespace SimplifiedUpdater.Core.Merge;

/// <summary>What one shader archive's merge turned out to be.</summary>
/// <param name="Carried">Resources the mod had that the game's archive did not — the mod's own.</param>
/// <param name="Preserved">Resources the game already had, kept at the new version.</param>
public sealed record ShaderMergeResult(string Name, int Carried, int Preserved, int Total);

/// <summary>
/// Rebuilding a mod's shader archive on top of the installed game's.
///
/// These archives hold the game's shaders and nothing else until a mod is made:
/// a mod's own resources — a decal, a livery, a logo — are added alongside them
/// under identifiers the archive never had. That makes the merge a plain rule
/// with no baseline needed. Start from the archive the installed game ships,
/// take everything from the mod's copy whose identifier is not already there,
/// and let the game's own resources stand.
///
/// The shaders are then the new game's, which is the point: a shader compiled
/// against the old build is exactly the kind of thing an update replaces, and
/// carrying the mod's copy over would put the old one back.
/// </summary>
public static class ShaderMerge
{
    /// <summary>
    /// Merge <paramref name="modErp"/> into the game's archive of the same name
    /// and write the result over <paramref name="modErp"/>.
    /// </summary>
    /// <returns>Null when the installed game has no archive by that name.</returns>
    public static ShaderMergeResult? Apply(string modErp, string gameShaderFolder)
    {
        var name = Path.GetFileName(modErp);
        var fresh = Path.Combine(gameShaderFolder, name);
        if (!File.Exists(fresh)) return null;

        // A merge reads the target and writes somewhere; the output cannot be
        // the file it is also reading as a source, so it lands beside it and
        // replaces it at the end.
        var staged = modErp + ".merged";
        try
        {
            var result = ErpFiles.Merge(fresh, [modErp], staged, overwrite: false);
            var tally = result.Tallies[0];

            File.Move(staged, modErp, overwrite: true);
            return new ShaderMergeResult(name, tally.Added, tally.Skipped, result.Total);
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
            throw;
        }
    }
}
