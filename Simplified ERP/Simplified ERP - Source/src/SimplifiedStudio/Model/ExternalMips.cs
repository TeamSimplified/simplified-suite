using System.IO;

namespace SimplifiedStudio.Model;

/// <summary>
/// Works out where a texture's external <c>.mipmaps</c> file lives on disk
/// without needing a game folder configured.
///
/// The surface resource already carries the answer: it stores a path relative
/// to the game root, e.g.
/// <c>character_package/drivers/male/redbull_verstappen/textures/foo.tif.2025.mipmaps</c>.
/// So when the ERP itself is opened from inside a game install, the root is
/// simply whichever of the ERP's parent folders makes that path resolve —
/// the ERP tells us where it came from. Only when the ERP is sitting outside
/// the game (a copy in a mod folder, say) do we need the Game folder from
/// Settings as a backup.
/// </summary>
internal static class ExternalMips
{
    // ERP folder → game root discovered from it. A single texture preview asks
    // once, but "Strip all mipmaps" asks for every texture in the archive, and
    // each miss costs a walk up to the drive root.
    private static readonly Dictionary<string, string> RootCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// The folder that <paramref name="relativeMipPath"/> hangs off, found by
    /// walking up from the ERP's own location. Null when the ERP isn't inside
    /// a tree that contains the file — caller should fall back to the
    /// configured game directory.
    /// </summary>
    public static string? FindRoot(string? erpPath, string? relativeMipPath)
    {
        if (string.IsNullOrWhiteSpace(erpPath) || string.IsNullOrWhiteSpace(relativeMipPath))
            return null;

        string? erpDir;
        try { erpDir = Path.GetDirectoryName(Path.GetFullPath(erpPath)); }
        catch { return null; }   // unusable path — treat as "not in the game"
        if (string.IsNullOrEmpty(erpDir)) return null;

        lock (Gate)
        {
            // A remembered root only counts while it still resolves: the same
            // folder could hold ERPs whose mips were since moved or deleted.
            if (RootCache.TryGetValue(erpDir, out var cached) && Resolves(cached, relativeMipPath))
                return cached;

            var found = Walk(erpDir, relativeMipPath);
            if (found is not null) RootCache[erpDir] = found;
            return found;
        }
    }

    /// <summary>Forget every discovered root — call when a new ERP is opened.</summary>
    public static void ClearCache()
    {
        lock (Gate) RootCache.Clear();
    }

    /// <summary>
    /// Climb from <paramref name="startDir"/> towards the drive root, looking
    /// for the ancestor the relative path hangs off. An ancestor holding the
    /// actual file wins outright. Failing that we accept one holding the
    /// path's <em>folder</em> — that still pins the root correctly for a
    /// texture whose .mipmaps file doesn't exist yet, which is what an import
    /// needs in order to write it into the right place.
    /// </summary>
    private static string? Walk(string startDir, string relativeMipPath)
    {
        var relativeDir = Path.GetDirectoryName(relativeMipPath);
        string? folderOnly = null;

        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Resolves(dir, relativeMipPath)) return dir;

            if (folderOnly is null && !string.IsNullOrEmpty(relativeDir))
            {
                try
                {
                    if (Directory.Exists(Path.Combine(dir, relativeDir))) folderOnly = dir;
                }
                catch { /* unreadable — keep climbing */ }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return folderOnly;
    }

    private static bool Resolves(string root, string relativeMipPath)
    {
        try { return File.Exists(Path.Combine(root, relativeMipPath)); }
        catch { return false; }
    }
}
