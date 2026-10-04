using System.IO;
using Simplified.Shared;

namespace SimplifiedLauncher.Mods;

/// <summary>What staging had to do to a freshly unpacked mod.</summary>
public enum Staged
{
    /// <summary>It already had its game folder in the right place.</summary>
    Already,

    /// <summary>It had one, buried. Lifted to the mod's root.</summary>
    Lifted,

    /// <summary>A folder called something else held the game files. Renamed.</summary>
    Renamed,

    /// <summary>The game files were loose at the mod's root. Gathered into one.</summary>
    Gathered,

    /// <summary>Nothing in it was recognisable, so nothing was moved.</summary>
    Unrecognised,
}

/// <summary>
/// Gives every installed mod the same shape: its game files under a folder named
/// <c>F1 25</c> at the mod's root, whatever the person who packaged it called it.
///
/// Mods are packaged by hand and the folder gets called <c>mod</c>, or
/// <c>F1 25 files</c>, or the mod's own name, or nothing at all with the files
/// loose at the top. Install has to copy a known thing into a known place, so
/// the variety is dealt with once, here, at import — rather than every later
/// step having to guess again.
///
/// Recognition is by content, not by name, because the name is exactly what
/// cannot be relied on. A folder is the game's if it holds something only the
/// game holds: <c>shader_package_2025</c>, one of the <c>_package</c> folders,
/// an <c>_asset_groups</c>. Those were taken from a real F1 25 install.
/// </summary>
public static class ModStaging
{
    /// <summary>
    /// The folder a mod's game files belong in, which is the install folder's own
    /// name. Taken from the suite's detector rather than written again, so there
    /// is one place that says what the game is called.
    /// </summary>
    public static string GameFolderName => GameDirDetector.GameFolderName;

    /// <summary>How deep to go looking. Three is past any real wrapping.</summary>
    private const int MaxDepth = 3;

    /// <summary>
    /// Folder names that only occur at the top of an F1 install. Enough to
    /// recognise a game root from one entry, which is all a mod touching a single
    /// area will give us.
    /// </summary>
    private static readonly string[] ExactMarkers =
        ["asset_groups", "actionmaps", "audio", "videos", "game_package", "character_package"];

    private static readonly string[] SuffixMarkers =
        ["_package", "_package_shared", "_asset_groups"];

    private static readonly string[] PrefixMarkers =
        ["shader_package_"];

    /// <summary>
    /// Put <paramref name="modRoot"/> into the shape everything downstream
    /// expects, and say what that took.
    ///
    /// Nothing is deleted and nothing is merged: every branch is a move, so a mod
    /// that this does not understand comes out exactly as it went in rather than
    /// rearranged on a guess.
    /// </summary>
    public static Staged Stage(string modRoot)
    {
        var wanted = Path.Combine(modRoot, GameFolderName);
        if (Directory.Exists(wanted)) return Staged.Already;

        // It may be in there under the right name, just too deep — an archive
        // that wrapped it in a folder the manifest test above would not strip.
        if (FindByName(modRoot) is { } buried)
        {
            Lift(buried, wanted);
            return Staged.Lifted;
        }

        // Otherwise go by what the folders hold rather than what they are called.
        if (FindByContent(modRoot) is not { } root) return Staged.Unrecognised;

        if (!root.Equals(modRoot, StringComparison.OrdinalIgnoreCase))
        {
            Lift(root, wanted);
            return Staged.Renamed;
        }

        // The game files are loose at the mod's root. Only what is positively
        // recognised moves in: the manifest, a read-me and anything else nobody
        // asked about stay where they are, because they are not the game's and
        // install has no business copying them into it.
        Directory.CreateDirectory(wanted);

        foreach (var folder in Directory.GetDirectories(modRoot))
            if (IsMarker(Path.GetFileName(folder)))
                Directory.Move(folder, Path.Combine(wanted, Path.GetFileName(folder)));

        return Staged.Gathered;
    }

    /// <summary>
    /// Move a folder to where it should have been, and tidy up behind it: a
    /// wrapper left standing empty is one more level for the next thing to look
    /// through.
    /// </summary>
    private static void Lift(string from, string to)
    {
        var parent = Path.GetDirectoryName(from);

        Directory.Move(from, to);

        while (parent is not null
               && !parent.Equals(Path.GetDirectoryName(to), StringComparison.OrdinalIgnoreCase)
               && IsEmpty(parent))
        {
            var next = Path.GetDirectoryName(parent);
            try { Directory.Delete(parent); } catch { break; }
            parent = next;
        }
    }

    private static bool IsEmpty(string folder)
    {
        try { return !Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch { return false; }
    }

    /// <summary>
    /// The shallowest folder actually called <c>F1 25</c>, case aside, or null.
    /// </summary>
    private static string? FindByName(string root) =>
        Walk(root).FirstOrDefault(d =>
            Path.GetFileName(d).Equals(GameFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The shallowest folder that holds something only the game holds, or null.
    /// The mod's own root counts, which is the loose-files case.
    /// </summary>
    private static string? FindByContent(string root) =>
        Walk(root).Prepend(root).FirstOrDefault(HoldsGameContent);

    private static bool HoldsGameContent(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Any(d => IsMarker(Path.GetFileName(d)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMarker(string name) =>
        ExactMarkers.Contains(name, StringComparer.OrdinalIgnoreCase)
        || SuffixMarkers.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        || PrefixMarkers.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every folder under <paramref name="root"/> down to <see cref="MaxDepth"/>,
    /// shallowest first — so the nearest candidate wins over a deeper one.
    /// </summary>
    private static IEnumerable<string> Walk(string root)
    {
        var level = new List<string> { root };

        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();

            foreach (var folder in level)
            {
                string[] children;
                try { children = Directory.GetDirectories(folder); }
                catch { continue; }

                foreach (var child in children)
                {
                    yield return child;
                    next.Add(child);
                }
            }

            level = next;
        }
    }
}
