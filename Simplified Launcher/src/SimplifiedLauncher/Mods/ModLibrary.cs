using System.ComponentModel;
using System.IO;

namespace SimplifiedLauncher.Mods;

/// <summary>One mod sitting in the library, as the list shows it.</summary>
/// <param name="Name">What to call it: the manifest's name, or the folder's.</param>
/// <param name="Author">Who made it, or empty when nothing says.</param>
/// <param name="Version">
/// Verbatim from the manifest, so <c>1.6</c> stays <c>1.6</c> and <c>v1.6</c>
/// stays <c>v1.6</c>. Whether to write the v is the author's choice and not
/// ours to correct, which is the same rule the suite packages under.
/// </param>
/// <param name="Folder">Absolute path of the mod's own folder.</param>
public sealed record InstalledMod(string Name, string Author, string Version, string Folder)
    : INotifyPropertyChanged
{
    private int _order;

    /// <summary>
    /// Where this one came in the ticking: 1 for the first, 2 for the next,
    /// and 0 for one that is not ticked at all.
    ///
    /// It lives on the mod rather than beside the list because the row has to
    /// hear about it changing. A number held in a table somewhere else would
    /// be read once, when the row was built, and then quietly go stale — and
    /// the one place it would go stale is a list long enough to scroll, where
    /// rows are built as they arrive.
    ///
    /// The order itself is the window's to keep. This is only where the answer
    /// is put so that a row can see it.
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            if (_order == value) return;

            _order = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Order)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// What this mod is, for equality, is the four things above — never the
    /// order, which is only where it sits in somebody's choosing.
    ///
    /// A record hashes every field it has, mutable ones included, and WPF keeps
    /// the selection in a hashed set. Change a selected item's hash and the
    /// selection can no longer find it to take it out again: unticking a row
    /// stops working, UnselectAll stops working, and nothing anywhere throws.
    /// Measured on the real window before this was written, which is the only
    /// reason it is known.
    /// </summary>
    public bool Equals(InstalledMod? other) =>
        other is not null
        && Name == other.Name
        && Author == other.Author
        && Version == other.Version
        && Folder == other.Folder;

    public override int GetHashCode() => HashCode.Combine(Name, Author, Version, Folder);
}

/// <summary>
/// The <c>mods</c> folder beside the exe: one folder per mod, named the way the
/// suite names the zip it exports — the mod's name, " - ", then its version.
///
/// The folder is the record. There is no index file to fall out of step with
/// what is on disk, so a mod can be removed by deleting its folder and added by
/// dropping an archive on the window, and the list says the same thing either
/// way.
/// </summary>
public static class ModLibrary
{
    /// <summary>
    /// Beside the exe, like the config. AppContext.BaseDirectory is right for
    /// both an ordinary build and a single-file one.
    /// </summary>
    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, "mods");

    /// <summary>
    /// Everything installed, by name. Missing folder means nothing installed
    /// rather than an error — a fresh copy of the app has no mods folder until
    /// the first import makes one.
    /// </summary>
    public static IReadOnlyList<InstalledMod> Read()
    {
        if (!Directory.Exists(FolderPath)) return [];

        var found = new List<InstalledMod>();

        foreach (var folder in Directory.EnumerateDirectories(FolderPath))
        {
            // A dot in front means the folder is the launcher's own business —
            // an import still being unpacked, or one a crash left behind.
            if (Path.GetFileName(folder).StartsWith('.')) continue;

            try { found.Add(Describe(folder)); }
            catch { /* one unreadable folder is not the whole list */ }
        }

        return found
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// What a folder says it is. The manifest inside it if there is one, and the
    /// folder's own name if there is not — which is also what the folder was
    /// named from, so the two agree by construction.
    /// </summary>
    private static InstalledMod Describe(string folder)
    {
        var manifest = ReadManifest(folder);
        var fallback = Path.GetFileName(folder);

        return new InstalledMod(
            ModManifest.Text(manifest?.Title) ?? fallback,
            ModManifest.Text(manifest?.Author) ?? "",
            ModManifest.Text(manifest?.Version) ?? "",
            folder);
    }

    /// <summary>
    /// Delete an installed mod, folder and all. True if it is gone afterwards.
    ///
    /// The path is checked against the library before anything is removed. The
    /// mod being deleted is one this app put there and the caller is a menu on a
    /// row, so it should always pass — which is the reason to check rather than a
    /// reason not to. Recursive deletes aimed by a path are worth one guard.
    /// </summary>
    public static bool Remove(InstalledMod mod)
    {
        var library = Path.GetFullPath(FolderPath);
        var folder = Path.GetFullPath(mod.Folder);

        // Directly inside the library, and not the library itself: a mod is
        // always exactly one level down.
        if (!Path.GetDirectoryName(folder)?.Equals(library, StringComparison.OrdinalIgnoreCase) ?? true)
            return false;

        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch
        {
            return !Directory.Exists(folder);
        }
    }

    /// <summary>The manifest in an installed mod's folder, or null.</summary>
    public static ModManifest? ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ModManifest.FileName);
        if (!File.Exists(path)) return null;

        try { return ModManifest.Parse(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>
    /// What to call a mod's folder: the name, then " - ", then the version — the
    /// way the suite names the zip it exports. The version goes on verbatim and
    /// is left off entirely, dash and all, when there is not one.
    ///
    /// The dash is what tells a reader where the name stops. "Formula Fantasy 25
    /// Season 2 2.0" does not say which part is the version; with the dash in it,
    /// it does.
    ///
    /// With no manifest to go on it is <paramref name="fallback"/> — the archive's
    /// own file name, or the dropped folder's. That one is left exactly as it was
    /// found: somebody named it, and second-guessing where a dash belongs in it
    /// would be inventing information.
    /// </summary>
    public static string FolderNameFor(ModManifest? manifest, string fallback)
    {
        if (ModManifest.Text(manifest?.Title) is not { } title)
            return SafeName(fallback);

        var version = ModManifest.Text(manifest?.Version);
        return version is null ? SafeName(title) : $"{SafeName(title)} - {SafeName(version)}";
    }

    /// <summary>
    /// The next free <c>Imported mod - N</c>.
    ///
    /// For a folder that arrives called F1 25, which is the game's own folder
    /// name rather than anything about the mod inside it. Every such drop would
    /// otherwise be called the same thing and replace the last one, so they are
    /// numbered instead. Counting up from what is already there rather than
    /// from how many mods there are, so deleting the third does not make the
    /// next import collide with the fourth.
    /// </summary>
    public static string NextImportedName()
    {
        var taken = Directory.Exists(FolderPath)
            ? Directory.EnumerateDirectories(FolderPath).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        for (var n = 1; ; n++)
        {
            var name = $"{ImportedPrefix}{n}";
            if (!taken.Contains(name)) return name;
        }
    }

    /// <summary>What a folder dropped in as F1 25 gets called instead.</summary>
    public const string ImportedPrefix = "Imported mod - ";

    /// <summary>
    /// A name the filesystem will take. Same approach the suite's packagers use:
    /// what Windows refuses becomes a dash, and leading or trailing dots and
    /// spaces come off, because Windows quietly drops those and the folder would
    /// then not be the one we went looking for.
    /// </summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray())
            .Trim()
            .Trim('.')
            .Trim();

        return cleaned.Length == 0 ? "mod" : cleaned;
    }
}
