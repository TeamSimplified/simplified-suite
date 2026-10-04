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
/// Takes an archive and leaves a mod in the library.
///
/// Format is decided by what is inside the file rather than by its extension,
/// so "any archive" means any archive: zip, 7z, rar, tar, gzip and the older
/// shapes too, under whatever name they happen to carry. A .zip renamed to
/// .serps still opens, which matters because that is a thing people do.
///
/// Extraction goes to a staging folder first and only then becomes the mod. An
/// import that fails halfway therefore costs nothing — in particular it cannot
/// destroy the copy already installed, which a delete-then-extract would do the
/// one time the archive turned out to be corrupt.
/// </summary>
public static class ModImport
{
    /// <summary>
    /// Install one archive. Returns what happened rather than throwing: a bad
    /// archive is an ordinary outcome here, not an exceptional one.
    /// </summary>
    public static ImportResult Install(string archivePath)
    {
        if (!File.Exists(archivePath))
            return new ImportResult(null, $"{Path.GetFileName(archivePath)} isn't there any more.");

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
                refused = ExtractInto(archivePath, staging);
            }
            catch (Exception e)
            {
                return new ImportResult(null,
                    $"{Path.GetFileName(archivePath)} couldn't be read as an archive. ({e.GetType().Name})");
            }

            var content = ContentRoot(staging);
            var manifest = ModLibrary.ReadManifest(content);

            // Still in staging, so a mod that cannot be made sense of has not
            // touched the library yet.
            var staged = ModStaging.Stage(content);

            var target = Path.Combine(ModLibrary.FolderPath, ModLibrary.FolderNameFor(manifest, archivePath));

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
                $"{Path.GetFileName(archivePath)} couldn't be installed. ({e.GetType().Name})");
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
