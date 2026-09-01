using System.IO;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// An open Simplified Studio project. While open, contents live in
/// <see cref="TempDir"/>. The <c>.simplified</c> zip on disk is only touched
/// on Save (rezipped from the temp dir) and Open (extracted to temp).
///
/// On-disk layout inside the .simplified zip / temp dir:
/// <code>
/// info.json
/// ERPs/
///     011--Wings/
///         001--front_wing.erp
///     021--Body/
///     099--loose_part.erp      ← root level = uncategorised
/// </code>
///
/// One folder holds every part exactly once; <see cref="ProjectInfo.Enabled"/>
/// says which are on. A part is addressed by its path relative to
/// <see cref="ErpsFolder"/>, so two categories may hold same-named parts.
/// </summary>
public sealed class SimplifiedProject
{
    /// <summary>Path to the <c>.simplified</c> file on disk. Null until first Save As.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Working temp directory mirroring the project's tree.</summary>
    public string TempDir { get; set; } = "";

    public ProjectInfo Info { get; set; } = new();

    /// <summary>Every part in the project, categorised or not.</summary>
    public string ErpsFolder => Path.Combine(TempDir, ProjectStore.ErpsFolderName);

    /// <summary>UI-friendly name. "Untitled project" until first Save As.</summary>
    public string DisplayName => ZipPath is null
        ? "Untitled project"
        : Path.GetFileNameWithoutExtension(ZipPath);

    /// <summary>
    /// Category folders directly under <c>ERPs/</c>, in prefix order. Only one
    /// level is meaningful — that keeps "same parent folder", which is what
    /// scopes a conflict group, unambiguous.
    /// </summary>
    public IEnumerable<string> CategoryFolderNames
    {
        get
        {
            if (!Directory.Exists(ErpsFolder)) return Enumerable.Empty<string>();
            return Directory.EnumerateDirectories(ErpsFolder)
                            .Select(Path.GetFileName)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Select(n => n!)
                            .OrderBy(n => n, PartPrefix.Comparer);
        }
    }

    /// <summary>
    /// Every part, as a path relative to <c>ERPs/</c>, in the order they merge:
    /// uncategorised parts first by their own prefix, then each category folder
    /// in prefix order with its parts in theirs.
    /// </summary>
    public IEnumerable<string> AllPartPaths
    {
        get
        {
            if (!Directory.Exists(ErpsFolder)) yield break;

            foreach (var f in PartsIn(ErpsFolder))
                yield return f;

            foreach (var folder in CategoryFolderNames)
                foreach (var f in PartsIn(Path.Combine(ErpsFolder, folder)))
                    yield return Path.Combine(folder, f);
        }
    }

    /// <summary>The parts switched on, in merge order. Names with no file are skipped.</summary>
    public IEnumerable<string> EnabledPartPaths
    {
        get
        {
            var on = new HashSet<string>(Info.Enabled, StringComparer.OrdinalIgnoreCase);
            return AllPartPaths.Where(on.Contains);
        }
    }

    /// <summary>Absolute path of a part given its path relative to <c>ERPs/</c>.</summary>
    public string PathOf(string partPath) => Path.Combine(ErpsFolder, partPath);

    /// <summary>
    /// The reverse: a part's path relative to <c>ERPs/</c> from its absolute
    /// one. Returns the bare file name for anything outside the folder.
    /// </summary>
    public string RelativePartPath(string absolutePath)
    {
        var root = Path.GetFullPath(ErpsFolder);
        var full = Path.GetFullPath(absolutePath);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(absolutePath);
    }

    /// <summary>Absolute paths of every enabled part, in merge order.</summary>
    public IEnumerable<string> EnabledErpPaths => EnabledPartPaths.Select(PathOf);

    /// <summary>
    /// The category folder a part sits in, or "" when it's at the root. This is
    /// what scopes its conflict group.
    /// </summary>
    public static string CategoryOf(string partPath)
    {
        var dir = Path.GetDirectoryName(partPath);
        return string.IsNullOrEmpty(dir) ? "" : dir;
    }

    private static IEnumerable<string> PartsIn(string dir)
    {
        if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.erp")
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n, PartPrefix.Comparer);
    }
}
