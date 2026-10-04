using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplifiedLauncher.Mods;

/// <summary>What came of backing the game up.</summary>
/// <param name="Folder">The backup folder, whether or not anything went in it.</param>
/// <param name="Saved">Files copied out of the game this time.</param>
/// <param name="Already">
/// Files a previous launch had already saved, and which were deliberately left
/// alone. See <see cref="GameBackup"/> for why that is the safe answer.
/// </param>
/// <param name="FilesToDelete">
/// Files the mods bring that the game has not got, counted over every launch
/// since the backup was taken.
/// </param>
/// <param name="FoldersToDelete">
/// Folders the mods bring that the game has not got, the same way.
/// </param>
/// <param name="Problem">Why there is no usable backup. Null when there is one.</param>
public sealed record BackedUp(
    string Folder,
    int Saved = 0,
    int Already = 0,
    int FilesToDelete = 0,
    int FoldersToDelete = 0,
    string? Problem = null);

/// <summary>
/// Takes a copy of everything the mods are about to overwrite: for every file in
/// the install folder, the game's own copy of that same file, kept at the same
/// path under <c>backup\F1 25\</c> beside the exe.
///
/// Only what is about to be overwritten. Copying the whole game would be tens of
/// gigabytes to stand behind a mod that replaces four files.
///
/// A file already in the backup is never written over, and that rule is most of
/// what makes this safe to run twice. The backup holds the game as it was before
/// any mod touched it; the second launch reads a game that is already modded, so
/// saving again would overwrite the original with the mod's own file and quietly
/// throw away the only copy of what was there. First one wins, and the backup
/// stays true until it is put back and cleared.
///
/// The rest of what makes it safe is <c>delete.json</c>: the files and the
/// folders the mods brought that the game had not got. A backup can hold a file
/// as it was, but it cannot hold the absence of one, so those are written down
/// instead — they are what restoring has to delete rather than put back, and the
/// name says so.
///
/// Writing them down is also what keeps the backup honest on the second launch.
/// Such a file has no counterpart to save the first time and does have one the
/// next, because the install put it there, and nothing about it then says it is
/// not the game's own. Measured before the list existed: the second launch saved
/// a mod's new car as if the game had shipped it.
/// </summary>
public static class GameBackup
{
    /// <summary>Beside the mods folder and the install folder, which is beside the exe.</summary>
    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, "backup");

    /// <summary>
    /// Save the game's copy of every file <paramref name="install"/> is about to
    /// put over it, and note down what it is about to add.
    /// </summary>
    /// <param name="game">The game's own folder, the one called <c>F1 25</c>.</param>
    /// <param name="install">The install folder, as <see cref="ModStack"/> left it.</param>
    public static BackedUp Take(string game, string install)
    {
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
            return new BackedUp(FolderPath, Problem: "The game folder isn't where it was.");

        var coming = Path.Combine(install, ModStaging.GameFolderName);
        if (!Directory.Exists(coming))
            return new BackedUp(FolderPath, Problem: "There is nothing staged to back up against.");

        try
        {
            var saved = 0;
            var already = 0;

            var ours = Noted();
            var was = (ours.Files.Count, ours.Folders.Count);

            // Folders first, because a folder the game has not got is the reason
            // every file under it has no counterpart either, and the answer for
            // the folder has to be written down before the install creates it.
            //
            // Every level of it, not only the topmost: restoring takes them away
            // deepest first and only while they are empty, which is what stops it
            // removing a folder somebody has since put something of their own in.
            foreach (var folder in Directory.GetDirectories(coming, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(coming, folder);

                if (ours.Folders.Contains(relative)) continue;
                if (!Directory.Exists(Path.Combine(game, relative))) ours.Folders.Add(relative);
            }

            foreach (var file in Directory.GetFiles(coming, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(coming, file);
                var theirs = Path.Combine(game, relative);
                var kept = Path.Combine(FolderPath, ModStaging.GameFolderName, relative);

                // Ours, from an earlier launch. It is in the game because this
                // app put it there, so backing it up now would file a mod's work
                // as the game's own and there would be no way to tell afterwards.
                if (ours.Files.Contains(relative)) continue;

                // The mod brings a file the game has not got. Nothing to save,
                // and worth writing down: putting the game back means removing
                // it rather than restoring it.
                if (!File.Exists(theirs))
                {
                    ours.Files.Add(relative);
                    continue;
                }

                if (File.Exists(kept))
                {
                    already++;
                    continue;
                }

                Keep(theirs, kept);
                saved++;
            }

            if (was != (ours.Files.Count, ours.Folders.Count)) Remember(ours);

            return new BackedUp(FolderPath, saved, already, ours.Files.Count, ours.Folders.Count);
        }
        catch (Exception e)
        {
            return new BackedUp(FolderPath, Problem: $"The game couldn't be backed up. ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// Copy one of the game's files into the backup, under a part name first.
    ///
    /// A copy is not one operation and a crash during one leaves a file that is
    /// the right length in the listing and half the bytes inside. Whereas a
    /// rename either happened or did not, so what carries the real name is only
    /// ever a complete copy — which matters here more than anywhere else in the
    /// app, because this is the only copy of the game's own file.
    /// </summary>
    private static void Keep(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        var part = to + PartSuffix;

        File.Copy(from, part, overwrite: true);
        File.Move(part, to, overwrite: true);
    }

    /// <summary>Marks a copy still being made.</summary>
    private const string PartSuffix = ".part";

    /// <summary>
    /// What the mods have put into the game that was not there before: paths
    /// under the game folder, which is what restoring will have to delete.
    /// </summary>
    private sealed class Leftovers
    {
        [JsonPropertyName("files")] public List<string> Files { get; set; } = [];

        [JsonPropertyName("folders")] public List<string> Folders { get; set; } = [];
    }

    /// <summary>The same thing while it is being worked on, where order does not matter.</summary>
    private sealed record Noting(HashSet<string> Files, HashSet<string> Folders);

    /// <summary>
    /// Where the list lives. Beside the saved files rather than inside them, so
    /// it cannot be mistaken for one of the game's.
    /// </summary>
    private static string Record => Path.Combine(FolderPath, "delete.json");

    /// <summary>
    /// What has been noted so far. Nothing when there is no record, and nothing
    /// when there is one that will not read — which costs a file being backed up
    /// that should not have been, and is better than refusing to launch over a
    /// list.
    /// </summary>
    private static Noting Noted()
    {
        var noted = new Noting(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        try
        {
            if (File.Exists(Record) && JsonSerializer.Deserialize<Leftovers>(File.ReadAllBytes(Record)) is { } held)
            {
                foreach (var path in held.Files) noted.Files.Add(path);
                foreach (var path in held.Folders) noted.Folders.Add(path);
            }
        }
        catch { /* a list that will not read is a list we have not got */ }

        return noted;
    }

    /// <summary>Write the list back, under a part name first, as the copies are.</summary>
    private static void Remember(Noting noted)
    {
        Directory.CreateDirectory(FolderPath);

        var part = Record + PartSuffix;

        File.WriteAllBytes(part, JsonSerializer.SerializeToUtf8Bytes(
            new Leftovers
            {
                Files = [.. noted.Files.Order(StringComparer.OrdinalIgnoreCase)],

                // Deepest last, so reading the file from the bottom up is the
                // order they have to be removed in.
                Folders = [.. noted.Folders.OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                                           .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)],
            },
            Writing));

        File.Move(part, Record, overwrite: true);
    }

    /// <summary>Indented, because somebody will open it to see what it says.</summary>
    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };
}
