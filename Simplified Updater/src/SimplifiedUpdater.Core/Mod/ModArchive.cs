using System.IO.Compression;
using SharpCompress.Archives;

namespace SimplifiedUpdater.Core.Mod;

/// <summary>
/// A mod as its author packed it: .zip, .7z or .rar.
///
/// Unpacked to a working folder rather than held in memory. A mod carrying
/// every language file and a database is a couple of hundred megabytes before
/// anything is merged into it, and the job ends by writing all of it back out
/// as a zip — which is a file operation either way.
/// </summary>
public static class ModArchive
{
    /// <summary>The extensions a mod may arrive as, lowercase, with the dot.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".zip", ".7z", ".rar"];

    /// <summary>For a file dialog: <c>Mods (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar</c>.</summary>
    public static string FileFilter =>
        "Mod archives (" + string.Join(';', Extensions.Select(e => "*" + e)) + ")|" +
        string.Join(';', Extensions.Select(e => "*" + e));

    public static bool LooksLikeMod(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Unpack every file into <paramref name="destination"/>, keeping the
    /// archive's own folder layout. Returns each file's path relative to that
    /// folder, in archive order.
    /// </summary>
    public static List<string> ExtractTo(string archivePath, string destination, Action<string>? log = null)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);
        var written = new List<string>();

        using var archive = ArchiveFactory.OpenArchive(archivePath);

        // An archive is somebody else's file, and an entry is free to call
        // itself ..\..\windows\system32\something. Resolve where it wants to go
        // and make sure it stayed inside.
        string? Target(string? key)
        {
            if (key is null) return null;
            var full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return full;

            log?.Invoke($"Skipped {key} — it points outside the folder it was being unpacked into.");
            return null;
        }

        if (archive.IsSolid)
        {
            // A solid .7z or .rar compresses whole runs of files together, so
            // picking entries out of one at random re-decompresses the run each
            // time. Read straight through instead: one pass for the lot.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                if (Target(reader.Entry.Key) is not { } target) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target)) reader.WriteEntryTo(output);
                written.Add(Path.GetRelativePath(root, target));
            }
        }
        else
        {
            // Everything else — a .zip, a non-solid .7z — is addressable, and
            // ExtractAllEntries refuses to run on one.
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                if (Target(entry.Key) is not { } target) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = entry.OpenEntryStream())
                using (var output = File.Create(target)) input.CopyTo(output);
                written.Add(Path.GetRelativePath(root, target));
            }
        }

        return written;
    }

    /// <summary>
    /// Pack a folder up as a .zip, built beside the destination and moved into
    /// place at the end so a half-written archive never appears at the path
    /// somebody is watching.
    /// </summary>
    public static int WriteZip(string folder, string zipPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.zip.part");

        var count = 0;
        try
        {
            using (var stream = File.Create(temporary))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    // Zip entries are '/'-separated whatever wrote them.
                    var name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                    count++;
                }
            }

            File.Move(temporary, zipPath, overwrite: true);
            return count;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
            throw;
        }
    }
}
