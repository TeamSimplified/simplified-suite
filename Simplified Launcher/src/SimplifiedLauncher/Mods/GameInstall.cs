using System.IO;

namespace SimplifiedLauncher.Mods;

/// <summary>What came of putting the install folder into the game.</summary>
/// <param name="Files">Files written into the game.</param>
/// <param name="Problem">
/// What stopped it. A launch that gets this has a game part way through being
/// modded, which is what the backup is for.
/// </param>
public sealed record Installed(int Files = 0, string? Problem = null);

/// <summary>
/// Copies the install folder into the game: everything under
/// <c>install\F1 25\</c> lands at the same path inside the game's own folder.
///
/// The install folder was built to be the shape of the game for exactly this
/// reason — there is no mapping to do here, no decisions left, and nothing in
/// this file knows what a mod is. By the time anything is written into somebody's
/// game, every question about what should go where has been answered twice over
/// and written down.
///
/// Nothing is deleted and nothing is moved: the game keeps every file the mods
/// are silent about, which is nearly all of them.
/// </summary>
public static class GameInstall
{
    /// <summary>
    /// Write <paramref name="install"/> into <paramref name="game"/>.
    /// </summary>
    /// <param name="game">The game's own folder, the one called <c>F1 25</c>.</param>
    /// <param name="install">The install folder, as <see cref="ModStack"/> left it.</param>
    public static Installed Put(string game, string install)
    {
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
            return new Installed(Problem: "The game folder isn't where it was.");

        var coming = Path.Combine(install, ModStaging.GameFolderName);
        if (!Directory.Exists(coming))
            return new Installed(Problem: "There is nothing staged to install.");

        try
        {
            var files = 0;

            foreach (var file in Directory.GetFiles(coming, "*", SearchOption.AllDirectories))
            {
                var landing = Path.Combine(game, Path.GetRelativePath(coming, file));

                Directory.CreateDirectory(Path.GetDirectoryName(landing)!);
                Write(file, landing);
                files++;
            }

            return new Installed(files);
        }
        catch (Exception e)
        {
            return new Installed(Problem: $"The mods couldn't be installed. ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// Write one file into the game, under a part name first and renamed into
    /// place, as the backup saves them.
    ///
    /// A copy is not one operation. Killed in the middle of one, what is left in
    /// the game is a file of the right name and half the bytes, which the game
    /// will try to read as if it were whole. A rename either happened or did
    /// not. What is left then is a stray part file beside a game file that is
    /// still the one the backup knows about.
    /// </summary>
    private static void Write(string from, string to)
    {
        var part = to + PartSuffix;

        try
        {
            File.Copy(from, part, overwrite: true);
            File.Move(part, to, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* it is not ours to insist */ }
            throw;
        }
    }

    /// <summary>Marks a copy still being made.</summary>
    private const string PartSuffix = ".simplified-part";
}
