using System.IO;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// The notes and moodboards a project carries alongside its parts.
///
/// A mod is not only its ERPs. What the maker was going for, what is left to
/// do, the references they are working from — all of that normally lives in
/// scattered text files and PureRef windows, and is the first thing lost when a
/// project is handed over or picked up again a year later. Keeping it in the
/// <c>.simplified</c> means the project file really is the one place the mod
/// lives.
///
/// Two folders beside <c>ERPs/</c>:
/// <code>
/// info.json
/// ERPs/
/// Notes/
///     To do.md
/// Boards/
///     Livery references.pur
/// </code>
///
/// Neither needed a change to the format. A project is a zip extracted whole on
/// open and rezipped whole on save, so a folder nothing knows about round-trips
/// on its own — and packaging builds its entries from <c>ERPs/</c> by name, so
/// none of this can reach the people who install the mod.
/// </summary>
public static class ProjectWorkspace
{
    public const string NotesFolderName = "Notes";
    public const string BoardsFolderName = "Boards";

    public const string NoteExtension = ".md";
    public const string BoardExtension = ".pur";

    /// <summary>One note or board on disk.</summary>
    /// <param name="Name">What it is called, without the extension — what the user typed.</param>
    public sealed record Item(string Name, string Path, long Size, DateTime Modified)
    {
        public override string ToString() => Name;
    }

    /// <summary>What a new note starts as. A heading, so the file isn't a blank page.</summary>
    public static string StarterNote(string title) => $"# {title}\n\n";

    // ── Reading ─────────────────────────────────────────────────────────────

    public static string NotesFolder(SimplifiedProject project) =>
        Path.Combine(project.TempDir, NotesFolderName);

    public static string BoardsFolder(SimplifiedProject project) =>
        Path.Combine(project.TempDir, BoardsFolderName);

    /// <summary>Every note, by name. The folder is the authority — a project edited by hand still reads right.</summary>
    public static IReadOnlyList<Item> Notes(SimplifiedProject project) =>
        Read(NotesFolder(project), NoteExtension);

    /// <summary>Every board, by name.</summary>
    public static IReadOnlyList<Item> Boards(SimplifiedProject project) =>
        Read(BoardsFolder(project), BoardExtension);

    private static IReadOnlyList<Item> Read(string folder, string extension)
    {
        if (!Directory.Exists(folder)) return [];

        var items = new List<Item>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + extension))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }   // vanished between the listing and the stat

            items.Add(new Item(Path.GetFileNameWithoutExtension(path), path, info.Length, info.LastWriteTime));
        }

        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a new note. Returns its path; the name is made unique rather than
    /// overwriting one that is already there.
    /// </summary>
    public static Item CreateNote(SimplifiedProject project, string name)
    {
        var folder = NotesFolder(project);
        Directory.CreateDirectory(folder);

        var path = UniquePath(folder, Clean(name), NoteExtension);
        var title = Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, StarterNote(title));
        return Describe(path);
    }

    /// <summary>
    /// Start a new board by copying the blank one the app ships.
    ///
    /// PureRef has no command line — the only switches in the binary belong to
    /// its crash reporter — so a board cannot be asked for, only copied from one
    /// that already exists.
    /// </summary>
    public static Item CreateBoard(SimplifiedProject project, string name, Stream blankTemplate)
    {
        var folder = BoardsFolder(project);
        Directory.CreateDirectory(folder);

        var path = UniquePath(folder, Clean(name), BoardExtension);
        using (var file = File.Create(path)) blankTemplate.CopyTo(file);
        return Describe(path);
    }

    /// <summary>Bring an existing board or note in from anywhere on disk.</summary>
    public static Item Import(SimplifiedProject project, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        var folder = extension.Equals(BoardExtension, StringComparison.OrdinalIgnoreCase)
            ? BoardsFolder(project)
            : NotesFolder(project);

        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, Path.GetFileNameWithoutExtension(sourcePath), extension);
        File.Copy(sourcePath, path);
        return Describe(path);
    }

    /// <summary>
    /// Rename in place, keeping the extension. Returns the item at its new
    /// path, or the old one when the name would not actually change.
    /// </summary>
    public static Item Rename(Item item, string newName)
    {
        var folder = Path.GetDirectoryName(item.Path)!;
        var extension = Path.GetExtension(item.Path);
        var cleaned = Clean(newName);

        if (cleaned.Equals(item.Name, StringComparison.Ordinal)) return item;

        var path = UniquePath(folder, cleaned, extension);
        File.Move(item.Path, path);
        return Describe(path);
    }

    public static void Delete(Item item)
    {
        if (File.Exists(item.Path)) File.Delete(item.Path);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Item Describe(string path)
    {
        var info = new FileInfo(path);
        return new Item(Path.GetFileNameWithoutExtension(path), path, info.Length, info.LastWriteTime);
    }

    /// <summary>
    /// A file name from whatever the user typed. Names are theirs to choose, so
    /// only what the filesystem refuses is taken out.
    /// </summary>
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray())
            .Trim()
            .Trim('.');

        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    /// <summary>
    /// <c>name.md</c>, or <c>name 2.md</c> when that is taken. Counting up
    /// rather than refusing keeps "New note" from ever being a dead end.
    /// </summary>
    private static string UniquePath(string folder, string name, string extension)
    {
        var candidate = Path.Combine(folder, name + extension);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{name} {n++}{extension}");
        return candidate;
    }
}
