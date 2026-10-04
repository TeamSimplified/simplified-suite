using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// What came of trying to import one archive.
/// </summary>
/// <param name="Mod">The installed mod, or null when nothing was installed.</param>
/// <param name="Problem">Why not, in a form worth showing. Null on success.</param>
/// <param name="Refused">
/// Entries left out because their path pointed outside the mod's folder. Almost
/// always zero; worth counting rather than ignoring, because an archive that
/// does this is either broken or trying something.
/// </param>
/// <param name="Staging">
/// What had to be done to give the mod its <c>F1 25</c> folder. Not shown
/// anywhere: it is here because an import that came out
/// <see cref="Staged.Unrecognised"/> is the one worth looking at when a mod
/// later refuses to install.
/// </param>
public sealed record ImportResult(
    InstalledMod? Mod,
    string? Problem,
    int Refused = 0,
    Staged Staging = Staged.Already);

/// <summary>
/// Takes an archive, or a folder, and leaves a mod in the library.
///
/// Format is decided by what is inside the file rather than by its extension,
/// so "any archive" means any archive: zip, 7z, rar, tar, gzip and the older
/// shapes too, under whatever name they happen to carry. A .zip renamed to
/// .serps still opens, which matters because that is a thing people do.
///
/// A folder is the same import without the unpacking: people hand mods around
/// unzipped, and one dragged in should land exactly where its archive would have
/// put it rather than in some second shape of its own.
///
/// Extraction goes to a staging folder first and only then becomes the mod. An
/// import that fails halfway therefore costs nothing — in particular it cannot
/// destroy the copy already installed, which a delete-then-extract would do the
/// one time the archive turned out to be corrupt.
/// </summary>
public static class ModImport
{
    /// <summary>
    /// Install one dropped path, archive or folder. Returns what happened rather
    /// than throwing: a bad archive is an ordinary outcome here, not an
    /// exceptional one.
    /// </summary>
    public static ImportResult Install(string path)
    {
        // The only difference between the two is how the content gets into
        // staging — copied, or unpacked. Past that they are the same import,
        // which is why the rest of this is handed a way in rather than a file.
        if (Directory.Exists(path))
            return WrapsTheLibrary(path)
                ? new ImportResult(null, $"{Path.GetFileName(path)} is where the mods already live.")
                : Bring(path, Copy, FallbackFor(path));

        if (File.Exists(path))
            return Bring(path, ExtractInto, Path.GetFileNameWithoutExtension(path));

        return new ImportResult(null, $"{Path.GetFileName(path)} isn't there any more.");
    }

    /// <summary>
    /// What to call a dropped folder when nothing inside it says.
    ///
    /// Its own name, unless its own name is the game's. A folder called
    /// <c>F1 25</c> is named after the game rather than after the mod inside it,
    /// so every one of them would arrive under the same name and quietly replace
    /// the last. Those get numbered instead.
    /// </summary>
    private static string FallbackFor(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return name.Equals(ModStaging.GameFolderName, StringComparison.OrdinalIgnoreCase)
            ? ModLibrary.NextImportedName()
            : name;
    }

    /// <summary>
    /// Whether a dropped folder is the library, or has it somewhere inside it ~
    /// someone dragging in the launcher's own folder, or the drive it sits on.
    ///
    /// Refused rather than attempted. Staging is created inside the library, so
    /// this is a folder being copied into a place that is itself inside what is
    /// being copied, and the mod it produced would be every mod already
    /// installed.
    /// </summary>
    private static bool WrapsTheLibrary(string folder)
    {
        var outer = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var library = Path.GetFullPath(ModLibrary.FolderPath);

        return library.Equals(outer, StringComparison.OrdinalIgnoreCase)
            || library.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything from "the content is in staging" onwards, which is everything
    /// that decides what the mod is called and where it ends up, and is the same
    /// whichever way the content got there.
    /// </summary>
    /// <param name="source">The archive or folder, for the messages.</param>
    /// <param name="into">
    /// How to get the content into staging: returns how many entries it refused.
    /// </param>
    /// <param name="fallback">What to name the mod if it carries no manifest.</param>
    private static ImportResult Bring(string source, Func<string, string, int> into, string fallback)
    {
        Directory.CreateDirectory(ModLibrary.FolderPath);

        // Dot-prefixed so a half-finished import is skipped by the library
        // listing rather than appearing as a mod called .import-something.
        var staging = Path.Combine(ModLibrary.FolderPath, "." + ImportFolderPrefix + Guid.NewGuid().ToString("n"));

        try
        {
            Directory.CreateDirectory(staging);

            int refused;
            try
            {
                refused = into(source, staging);
            }
            catch (Exception e)
            {
                return new ImportResult(null,
                    $"{Path.GetFileName(source)} couldn't be read. ({e.GetType().Name})");
            }

            var content = ContentRoot(staging);
            var manifest = ModLibrary.ReadManifest(content);

            // Still in staging, so a mod that cannot be made sense of has not
            // touched the library yet.
            var staged = ModStaging.Stage(content);

            var target = Path.Combine(ModLibrary.FolderPath, ModLibrary.FolderNameFor(manifest, fallback));

            // Only now, with a complete extraction in hand, is the installed
            // copy worth disturbing. Re-importing is how a mod gets updated, so
            // the same name and version replacing itself is the expected case
            // rather than a collision to avoid.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(content, target);

            var name = ModManifest.Text(manifest?.Title) ?? Path.GetFileName(target);

            return new ImportResult(
                new InstalledMod(
                    name,
                    ModManifest.Text(manifest?.Author) ?? "",
                    ModManifest.Text(manifest?.Version) ?? "",
                    target),
                null,
                refused,
                staged);
        }
        catch (Exception e)
        {
            return new ImportResult(null,
                $"{Path.GetFileName(source)} couldn't be installed. ({e.GetType().Name})");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* a staging folder left behind is tidied by the next import */ }
        }
    }

    /// <summary>Marks a staging folder, so one left by a crash can be recognised.</summary>
    public const string ImportFolderPrefix = "import-";

    /// <summary>
    /// Copy a dropped folder's contents into staging, and return how many files
    /// were refused for landing outside it.
    ///
    /// The contents rather than the folder itself, so a folder called
    /// <c>F1 25</c> arrives as game content at the staging root and staging
    /// gathers it into an <c>F1 25</c> of its own — the shape a zip of the same
    /// thing would have produced. One rule for both, rather than a second one
    /// here.
    ///
    /// Copied, not moved: the drag said Copy, and an import has no business
    /// emptying a folder on somebody's desktop.
    ///
    /// The list is taken before any of it is written, because what is written to
    /// is a folder inside the library, and the library may be inside what is
    /// being copied from.
    ///
    /// The containment check is the extractor's, for the reason it is there: a
    /// folder is likelier to be honest than an archive off the internet, but a
    /// junction pointing somewhere else costs nothing to refuse.
    /// </summary>
    private static int Copy(string folder, string into)
    {
        var refused = 0;
        var root = Path.GetFullPath(into);
        var from = Path.GetFullPath(folder);

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(from, file)));

            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                refused++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        return refused;
    }

    /// <summary>
    /// Unpack everything into <paramref name="into"/>, and return how many
    /// entries were refused for pointing somewhere they should not.
    ///
    /// Forward-only in both cases, through one reader, which is what makes a
    /// solid archive decompress once instead of once per entry.
    ///
    /// Two ways in, because the library has two and neither covers everything.
    /// A streaming reader takes zip, rar (solid included), tar, the compressed
    /// tars and the older shapes, and it is also the only one that reads a
    /// .tar.gz properly — the random-access side sees the gzip as a single entry
    /// and never reaches the tar inside it. 7z is the other way round, and says
    /// so by refusing to be streamed at all.
    ///
    /// Each file is written here rather than through the library's own
    /// WriteToDirectory. That method is where CVE-2026-44788 was — a path
    /// traversal fixed in 0.48.0, which is the version this builds against —
    /// and a check of our own costs nothing and does not depend on having got
    /// the version right.
    /// </summary>
    private static int ExtractInto(string archivePath, string into)
    {
        FileStream? file = null;
        IArchive? archive = null;
        IReader? reader = null;

        try
        {
            file = File.OpenRead(archivePath);

            try
            {
                reader = ReaderFactory.OpenReader(file);
            }
            catch (InvalidFormatException)
            {
                // 7z keeps its table of contents at the end, so it cannot be
                // streamed and its reader wants the whole file to seek around
                // in. That is the single case ExtractAllEntries exists for, and
                // the library refuses it for anything else.
                //
                // The stream is reopened rather than rewound: the first attempt
                // owns it and may have read into it.
                file.Dispose();
                file = File.OpenRead(archivePath);
                archive = ArchiveFactory.OpenArchive(file);
                reader = archive.ExtractAllEntries();
            }

            return Drain(reader, into);
        }
        finally
        {
            reader?.Dispose();
            archive?.Dispose();
            file?.Dispose();
        }
    }

    /// <summary>
    /// Walk a reader to the end, writing each file.
    ///
    /// Shared by both of the routes above, which is the point of choosing
    /// between them at the door: everything past that door is one loop.
    /// </summary>
    private static int Drain(IReader reader, string into)
    {
        var refused = 0;
        var root = Path.GetFullPath(into);

        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory) continue;
            if (reader.Entry.Key is not { } key) continue;

            if (Relative(key) is not { } relative)
            {
                refused++;
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));

            // The second half of the same check, after the combine rather than
            // before it: the one above reads the entry's own path, this one
            // reads where it actually landed.
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                refused++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var entry = reader.OpenEntryStream();
            using var output = File.Create(destination);
            entry.CopyTo(output);
        }

        return refused;
    }

    /// <summary>
    /// An entry's path as something safe to append, or null to refuse it.
    ///
    /// Archives from the internet contain absolute paths and <c>..</c> segments,
    /// sometimes by accident and sometimes not. Neither has any business in a
    /// mod, so both are refused outright instead of being cleaned up into
    /// something that merely looks harmless.
    /// </summary>
    private static string? Relative(string key)
    {
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        foreach (var part in parts)
        {
            if (part is "." or "..") return null;
            if (part.Contains(':')) return null;      // a drive letter, or an NTFS stream
        }

        return Path.Combine(parts);
    }

    /// <summary>
    /// Where the mod's files actually start.
    ///
    /// Plenty of archives wrap everything in one folder, which would otherwise
    /// become a pointless level inside the library and hide the manifest from
    /// anything looking at the mod's root. So a single wrapping folder is
    /// stepped into — but only when doing so finds a <see cref="ModManifest"/>,
    /// which is both the reason for stepping in and the proof it was a wrapper.
    ///
    /// Anything else is left exactly as the archive had it. A mod whose root
    /// holds one meaningful folder and no manifest — <c>F1 25/</c> on its own —
    /// would be ruined by unwrapping it, and the rule above is what stops that.
    /// </summary>
    private static string ContentRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, ModManifest.FileName))) return staging;

        var children = Directory.GetFileSystemEntries(staging);
        if (children.Length != 1 || !Directory.Exists(children[0])) return staging;

        return File.Exists(Path.Combine(children[0], ModManifest.FileName)) ? children[0] : staging;
    }
}
