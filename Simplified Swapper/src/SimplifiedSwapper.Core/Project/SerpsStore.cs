using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SimplifiedSwapper.Core.Project;

/// <summary>
/// Open / save / close a <c>.serps</c> template, and put a finished swap into
/// one.
///
/// Same shape as Studio's ProjectStore, and for the same reason: an archive is
/// a poor thing to edit in place, so it is extracted once on open, worked on as
/// a folder, and rebuilt on save.
/// </summary>
public static class SerpsStore
{
    public const string FileExtension  = ".serps";
    public const string ErpsFolderName = "ERPs";
    public const string InfoFileName   = "template.serps";

    private static string TempRoot
        => Path.Combine(Path.GetTempPath(), "simplified-swapper", "open");

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ─────────────────────── Open / Save / Close ──────────────────────

    /// <summary>
    /// Extract a template to a working folder and read what it says about
    /// itself. Both the ZIP comment and <c>template.serps</c> are picked up
    /// here, because both have to survive back out again.
    /// </summary>
    public static SerpsTemplate Open(string serpsPath)
    {
        if (!File.Exists(serpsPath))
            throw new FileNotFoundException("Template file not found.", serpsPath);

        var temp = AllocateTempDir();
        var template = new SerpsTemplate { SerpsPath = serpsPath, TempDir = temp };

        try
        {
            using (var zip = ZipFile.Open(serpsPath, ZipArchiveMode.Read, Encoding.UTF8))
            {
                template.Comment = zip.Comment ?? "";
                template.ShaderPackage = ShaderPackageIn(template.Comment);

                foreach (var entry in zip.Entries)
                {
                    var rel = SafeRelativePath(entry.FullName);
                    if (rel is null) continue;

                    // A directory entry carries an empty Name; recreate it so an
                    // empty category survives the round trip.
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(Path.Combine(temp, rel));
                        continue;
                    }

                    var dest = Path.Combine(temp, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            Directory.CreateDirectory(Path.Combine(temp, ErpsFolderName));

            var infoPath = Path.Combine(temp, InfoFileName);
            if (File.Exists(infoPath))
            {
                try
                {
                    template.Info = JsonSerializer.Deserialize<SerpsInfo>(File.ReadAllText(infoPath), Json)
                                    ?? new SerpsInfo();
                }
                catch
                {
                    // An unreadable template.serps costs the name in the title
                    // bar and nothing else — the ERPs are what matter, and the
                    // file itself is carried through to the save untouched.
                }
            }

            return template;
        }
        catch
        {
            Close(template);
            throw;
        }
    }

    /// <summary>
    /// Rebuild the archive from the working folder. With
    /// <paramref name="newPath"/> this is Save As.
    ///
    /// Written to a .tmp and swapped in, so a failure part-way through leaves
    /// the template that was there intact rather than truncated.
    /// </summary>
    public static void Save(SerpsTemplate template, string? newPath = null)
    {
        var target = newPath ?? template.SerpsPath;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No path to save to.");

        var tmpZip = target + ".tmp";
        if (File.Exists(tmpZip)) File.Delete(tmpZip);

        try
        {
            using (var fs = File.Create(tmpZip))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                // Folders first as directory entries, the way Studio writes a
                // template, so one whose parts were all removed still survives.
                foreach (var folder in template.CategoryFolderNames)
                    zip.CreateEntry($"{ErpsFolderName}/{folder}/");

                foreach (var part in template.AllPartPaths)
                    AddFile(zip, Path.Combine(template.ErpsFolder, part),
                            ErpsFolderName + "/" + part.Replace('\\', '/'));

                // Root files — template.serps and anything else the author put
                // there — copied across as they are. Nothing in this app has an
                // opinion about their contents.
                foreach (var file in Directory.EnumerateFiles(template.TempDir))
                    AddFile(zip, file, Path.GetFileName(file));

                zip.Comment = template.Comment;
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmpZip, target);
        }
        catch
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            throw;
        }

        template.SerpsPath = target;
    }

    private static void AddFile(ZipArchive zip, string sourcePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var fs = File.OpenRead(sourcePath);
        fs.CopyTo(es);
    }

    /// <summary>Discard an open template: remove its working folder.</summary>
    public static void Close(SerpsTemplate template)
    {
        try
        {
            if (Directory.Exists(template.TempDir))
                Directory.Delete(template.TempDir, recursive: true);
        }
        catch
        {
            // Best-effort — the next CleanupStaleTempDirs sweep will get it.
        }
    }

    /// <summary>
    /// Remove working folders left by sessions that crashed or were killed.
    /// A template runs to a couple of hundred megabytes extracted, so this is
    /// worth doing at startup.
    /// </summary>
    public static int CleanupStaleTempDirs(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempRoot)) return 0;
        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(TempRoot))
        {
            try
            {
                if (new DirectoryInfo(dir).LastWriteTimeUtc >= cutoff) continue;
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch { /* skip */ }
        }
        return removed;
    }

    // ─────────────────────── Placing a swap ───────────────────────────

    /// <summary>
    /// Put a finished swap into the template, as the only part of its folder.
    ///
    /// Any earlier folder for the same destination is removed first, whatever it
    /// was numbered, so swapping again supersedes the previous attempt rather
    /// than stacking up beside it: everything in one folder carrying group
    /// <c>00</c> merges together, so two swapped chassis side by side would both
    /// be applied, which is not a choice anyone means to make.
    /// </summary>
    /// <returns>The part's path relative to <c>ERPs/</c>.</returns>
    public static string PlacePart(SerpsTemplate template, string sourceErpPath,
                                   SwapParts.Destination destination)
    {
        if (!File.Exists(sourceErpPath))
            throw new FileNotFoundException("The swap produced no file to add.", sourceErpPath);

        // A template holds one swap of each kind, so out with what's already
        // there before the order is worked out — a folder left by an earlier
        // swap can't then influence where this one goes, or be left behind
        // under a number this template no longer produces.
        //
        // All of them, not the first: the numbers are ours but the file is the
        // user's, and two folders under one display name would otherwise leave
        // a second swapped chassis in the template for good.
        // Listed before the loop, not during it: CategoryFolderNames walks the
        // directory lazily, and deleting out from under that walk throws.
        var previous = template.CategoryFolderNames.Where(f =>
            SerpsPrefix.Display(f).Equals(destination.DisplayName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var stale in previous)
        {
            try { Directory.Delete(Path.Combine(template.ErpsFolder, stale), recursive: true); }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Couldn't clear the previous swap out of '{stale}': {ex.Message}", ex);
            }
        }

        var folderName = SerpsPrefix.Apply(destination.DisplayName,
                                           OrderFor(template, destination.ConflictGroup),
                                           destination.ConflictGroup);

        // Read the token before the folder exists, so the part being placed
        // can't be the thing that decides what to call itself.
        var partFileName = SwapParts.PartFileName(template.NamePrefix, destination);

        var folder = Path.Combine(template.ErpsFolder, folderName);
        Directory.CreateDirectory(folder);
        File.Copy(sourceErpPath, Path.Combine(folder, partFileName), overwrite: true);
        return Path.Combine(folderName, partFileName);
    }

    /// <summary>
    /// What to number a swap's folder so it lands directly after the stock ones
    /// it is an alternative to.
    ///
    /// It takes their highest order rather than the next one up. Sharing an
    /// order is not a clash — order is a sort key, ties break on the display
    /// name — and it means nothing else in the template has to be renumbered to
    /// make room, which would be rewriting folders the template's author
    /// arranged.
    ///
    /// With no folder of that group to sit after, it goes to the end: there is
    /// nothing here it replaces, so it belongs with the extras rather than in
    /// the middle of the list.
    /// </summary>
    private static int OrderFor(SerpsTemplate template, int conflictGroup)
    {
        var orders = template.CategoryFolderNames
                             .Select(f => (order: SerpsPrefix.OrderOf(f), group: SerpsPrefix.GroupOf(f)))
                             .Where(x => x.order != SerpsPrefix.NoOrder)
                             .ToList();

        var inGroup = orders.Where(x => x.group == conflictGroup).Select(x => x.order).ToList();
        if (inGroup.Count > 0) return inGroup.Max();

        return orders.Count > 0 ? Math.Min(orders.Max(x => x.order) + 1, 99) : 1;
    }

    // ─────────────────────── Helpers ──────────────────────────────────

    /// <summary>
    /// Pull <c>erp_file_string</c> out of the archive comment, which is where a
    /// template's shader package lives. Empty when it isn't there — an older or
    /// hand-made template is still worth opening; it just can't say whose it is.
    /// </summary>
    private static string ShaderPackageIn(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        try
        {
            using var doc = JsonDocument.Parse(comment);
            return doc.RootElement.TryGetProperty("erp_file_string", out var v) &&
                   v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// An entry's path under the working folder, or null when it points outside
    /// it. A ZIP can name <c>../</c> and a template is a file people pass
    /// around, so the path is checked rather than trusted.
    /// </summary>
    private static string? SafeRelativePath(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (normalized.Length == 0) return null;

        var rel = normalized.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel)) return null;

        return rel.Split(Path.DirectorySeparatorChar).Any(s => s == "..") ? null : rel;
    }

    private static string AllocateTempDir()
    {
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
