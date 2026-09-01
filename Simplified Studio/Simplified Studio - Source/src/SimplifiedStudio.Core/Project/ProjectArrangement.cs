using System.IO;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// The project's arrangement — which categories exist, in what order, and which
/// parts sit in each, in what order — as an ordered model the UI can drag rows
/// around in.
///
/// Since 1.3 there is no file describing this: the folders under <c>ERPs/</c>
/// are the categories, and the <c>00--00--</c> prefixes are the order.
/// <see cref="Load"/> reads that structure into the model and
/// <see cref="Save"/> writes the model back by renumbering and moving files, so
/// dropping a row somewhere new is ultimately a rename.
///
/// Conflict groups are preserved through a reorder: only the order half of a
/// prefix is rewritten, so moving a part never silently changes what it's an
/// alternative to.
/// </summary>
public static class ProjectArrangement
{
    /// <summary>Display name of the group holding parts that are in no category.</summary>
    public const string UncategorizedName = "Uncategorized";

    public sealed class CategoryGroup
    {
        /// <summary>
        /// The name as shown and edited. Carries a prefix when it came off disk;
        /// a category the user just made or renamed won't have one yet, and
        /// <see cref="Save"/> gives it one.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The folder this group currently is on disk, empty for one that
        /// doesn't exist yet. Without it <see cref="Save"/> couldn't tell a
        /// rename from a creation — <see cref="Name"/> has already changed by
        /// the time it runs.
        /// </summary>
        public string CurrentFolder { get; set; } = "";

        /// <summary>Part paths relative to <c>ERPs/</c>, in display order.</summary>
        public List<string> Parts { get; } = new();

        public bool IsUncategorized =>
            string.Equals(Name, UncategorizedName, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class State
    {
        /// <summary>Categories in order. The uncategorised group is kept last.</summary>
        public List<CategoryGroup> Groups { get; } = new();

        /// <summary>
        /// A part's position across the whole arrangement — categories in order,
        /// parts within them in order. One number, so a list can sort by it and
        /// come out exactly as Manage ERPs shows things. Unknown parts sort last.
        /// </summary>
        public int OrderOf(string partPath)
        {
            var at = 0;
            foreach (var group in Groups)
            {
                foreach (var part in group.Parts)
                {
                    if (string.Equals(part, partPath, StringComparison.OrdinalIgnoreCase)) return at;
                    at++;
                }
            }
            return int.MaxValue;
        }

        /// <summary>The category a part sits in, by display name. <see cref="UncategorizedName"/> when loose.</summary>
        public string CategoryOf(string partPath)
        {
            foreach (var group in Groups)
                foreach (var part in group.Parts)
                    if (string.Equals(part, partPath, StringComparison.OrdinalIgnoreCase))
                        return group.Name;
            return UncategorizedName;
        }
    }

    /// <summary>Read the folder structure into an ordered model.</summary>
    public static State Load(SimplifiedProject project)
    {
        var state = new State();

        foreach (var folder in project.CategoryFolderNames)
        {
            var group = new CategoryGroup { Name = folder, CurrentFolder = folder };
            foreach (var part in project.AllPartPaths)
            {
                if (string.Equals(SimplifiedProject.CategoryOf(part), folder, StringComparison.OrdinalIgnoreCase))
                    group.Parts.Add(part);
            }
            state.Groups.Add(group);
        }

        // Loose parts last, so the section without a name sits at the bottom.
        var loose = new CategoryGroup { Name = UncategorizedName };
        foreach (var part in project.AllPartPaths)
            if (SimplifiedProject.CategoryOf(part).Length == 0)
                loose.Parts.Add(part);
        state.Groups.Add(loose);

        return state;
    }

    /// <summary>
    /// Write the model back to disk: renumber every folder and part to match its
    /// position, and move parts into the folder they were dropped in. The
    /// project's selection follows each file.
    ///
    /// Returns the number of files and folders whose names changed, so a caller
    /// can tell whether anything actually happened.
    /// </summary>
    public static int Save(SimplifiedProject project, State state)
    {
        var changed = 0;

        // Folders first: a part's destination is expressed in terms of the
        // folder name, so the folders have to settle before parts move.
        var order = 1;
        var folderNames = new Dictionary<CategoryGroup, string>();
        foreach (var group in state.Groups)
        {
            if (group.IsUncategorized) { folderNames[group] = ""; continue; }

            // A rename that dropped the prefix keeps the conflict group the
            // folder already had — renaming a category shouldn't quietly change
            // what it's an alternative to.
            var conflict = PartPrefix.Has(group.Name)
                ? PartPrefix.ConflictOf(group.Name) ?? 0
                : PartPrefix.ConflictOf(group.CurrentFolder) ?? 0;
            var wanted = PartPrefix.Apply(group.Name, order++, conflict);

            if (group.CurrentFolder.Length == 0)
            {
                ProjectStore.CreateCategory(project, wanted);
                changed++;
            }
            else if (!string.Equals(wanted, group.CurrentFolder, StringComparison.Ordinal))
            {
                ProjectStore.RenameCategory(project, group.CurrentFolder, wanted);
                changed++;
            }

            group.Name = wanted;
            group.CurrentFolder = wanted;
            folderNames[group] = wanted;
        }

        // Parts: move into the right folder, then renumber to their position.
        foreach (var group in state.Groups)
        {
            var targetFolder = folderNames[group];
            var partOrder = 1;

            for (var i = 0; i < group.Parts.Count; i++)
            {
                var current = group.Parts[i];
                if (!File.Exists(project.PathOf(current))) continue;

                if (!string.Equals(SimplifiedProject.CategoryOf(current), targetFolder,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    current = ProjectStore.MovePart(project, current, targetFolder);
                    changed++;
                }

                var fileName = Path.GetFileName(current);
                var wanted = PartPrefix.ApplyOrder(fileName, partOrder++);
                if (!string.Equals(wanted, fileName, StringComparison.Ordinal))
                {
                    current = ProjectStore.RenamePart(project, current, wanted);
                    changed++;
                }

                group.Parts[i] = current;
            }
        }

        // A folder the model no longer lists has been deleted. Its parts were
        // re-homed in the model above and have already moved, so what's left is
        // an empty folder — but DeleteCategory rescues anything still in it
        // rather than taking an ERP down with the folder.
        var kept = folderNames.Values.Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in project.CategoryFolderNames.ToList())
        {
            if (kept.Contains(folder)) continue;
            ProjectStore.DeleteCategory(project, folder);
            changed++;
        }

        return changed;
    }

}
