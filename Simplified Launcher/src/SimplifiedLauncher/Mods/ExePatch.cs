using System.IO;
using Simplified.Language;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// The one change to the game's exe that makes it read language files from
/// the game folder.
///
/// The game keeps its text in an archive inside the exe and never looks for a
/// loose .lng. Where it builds the name of the folder it reads them from, the
/// exe writes "localisation/" with a few immediate moves, and changing one
/// letter of those — <c>tion</c> to <c>tioa</c> — sends it to
/// <c>localisatioa/</c> instead, a folder only mods have. That is where every
/// language mod puts its files, and where the merged ones go. Six bytes, the
/// same length either way, so nothing else in the exe moves. Found once in the
/// current build, and the same bytes the launcher before this one used.
///
/// Never done to the game's exe in place. A patched copy is put in the install
/// folder like any mod file, so the backup takes the game's own exe before the
/// install puts this one over it, and putting the game back puts the original
/// back. Only staged when there are language files to read.
/// </summary>
public static class ExePatch
{
    /// <summary>The bytes as the game ships them.</summary>
    private static readonly byte[] Unpatched = " tionf"u8.ToArray();

    /// <summary>The same bytes, patched.</summary>
    private static readonly byte[] Patched = " tioaf"u8.ToArray();

    /// <summary>
    /// Put a patched copy of the game's exe in <paramref name="installGame"/>,
    /// the install folder's <c>F1 25</c>. A copy a mod brought is patched
    /// rather than replaced, since that one is the mod's choice of exe.
    /// Throws, saying why, when there is no exe or no place to patch.
    /// </summary>
    public static void Stage(string? gameDir, string installGame)
    {
        var exe = LanguageFiles.FindExe(gameDir)
                  ?? throw new FileNotFoundException("The game's exe isn't in the game folder, so it can't be patched.");

        var staged = Path.Combine(installGame, Path.GetFileName(exe));
        var data = File.Exists(staged) ? File.ReadAllBytes(staged) : File.ReadAllBytes(exe);

        var at = data.AsSpan().IndexOf(Unpatched);
        if (at < 0)
        {
            // Already patched is as good as patched: the launcher before this
            // one could leave the game's exe that way.
            if (data.AsSpan().IndexOf(Patched) >= 0)
            {
                Write(staged, data);
                return;
            }

            throw new InvalidDataException(
                "The game's exe has changed where the language files are read, so the patch has nowhere to go.");
        }

        Patched.CopyTo(data.AsSpan(at));
        Write(staged, data);
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }
}
