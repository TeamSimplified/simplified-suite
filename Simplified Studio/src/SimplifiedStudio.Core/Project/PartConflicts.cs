using System.IO;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// Which parts are alternatives to each other, from the conflict digit of the
/// <c>000--</c> prefix.
///
/// Two rules, at two levels:
///
///   * <b>Parts</b> conflict when they share a parent folder and a non-zero
///     conflict digit. Scoping to the folder is what lets every category reuse
///     the same nine groups. Uncategorised parts at the root share one scope
///     of their own.
///
///   * <b>Folders</b> conflict with other folders by their own digit, and the
///     effect cascades: switching on any part inside a folder switches off
///     <em>every</em> part in every folder it conflicts with. So one click can
///     turn several parts off across several folders — a much wider blast
///     radius than the part-level rule, which only ever swaps one for one.
///
/// The rules hold whatever the package format is. A .serps ships every part,
/// enabled or not, so it carries alternatives together either way — which
/// leaves no reason for the selection to behave differently depending on how
/// the project will eventually be packaged.
///
/// Nothing here reads or writes the project; it answers "what would this turn
/// off", and the caller applies it.
/// </summary>
public static class PartConflicts
{
    /// <summary>
    /// The parts that must switch off for <paramref name="turningOn"/> to be on,
    /// given what's currently on. Paths are relative to <c>ERPs/</c>, as in
    /// <see cref="ProjectInfo.Enabled"/>. The part being turned on is never in
    /// the result.
    /// </summary>
    public static IReadOnlyList<string> Displaced(string turningOn, IEnumerable<string> currentlyOn)
    {
        var displaced = new List<string>();
        if (string.IsNullOrEmpty(turningOn)) return displaced;

        var folder      = SimplifiedProject.CategoryOf(turningOn);
        var partGroup   = PartConflictGroup(turningOn);
        var folderGroup = FolderConflictGroup(folder);

        foreach (var other in currentlyOn)
        {
            if (string.IsNullOrEmpty(other)) continue;
            if (string.Equals(other, turningOn, StringComparison.OrdinalIgnoreCase)) continue;

            var otherFolder = SimplifiedProject.CategoryOf(other);

            // Folder level: a different folder that shares a group takes its
            // whole contents with it, whatever the parts' own digits say.
            if (folderGroup is not null &&
                !string.Equals(otherFolder, folder, StringComparison.OrdinalIgnoreCase) &&
                FolderConflictGroup(otherFolder) == folderGroup)
            {
                displaced.Add(other);
                continue;
            }

            // Part level: same folder, same group.
            if (partGroup is not null &&
                string.Equals(otherFolder, folder, StringComparison.OrdinalIgnoreCase) &&
                PartConflictGroup(other) == partGroup)
            {
                displaced.Add(other);
            }
        }

        return displaced;
    }

    /// <summary>
    /// Trim a selection down to something the rules allow, keeping the earlier
    /// entries. Used when a selection arrives from outside — a project opened
    /// after its files were edited by hand, say — rather than one click at a time.
    /// </summary>
    public static List<string> Reconcile(IEnumerable<string> wanted)
    {
        var kept = new List<string>();
        foreach (var part in wanted)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (Displaced(part, kept).Count > 0) continue;   // loses to something already kept
            if (kept.Any(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(part);
        }
        return kept;
    }

    /// <summary>The group of a part, from the second half of its file name's prefix.</summary>
    public static int? PartConflictGroup(string partPath) =>
        PartPrefix.ConflictOf(Path.GetFileName(partPath) ?? "");

    /// <summary>The group of a category folder. Null at the root — it has no prefix to read.</summary>
    public static int? FolderConflictGroup(string folderName) =>
        string.IsNullOrEmpty(folderName) ? null : PartPrefix.ConflictOf(folderName);
}
