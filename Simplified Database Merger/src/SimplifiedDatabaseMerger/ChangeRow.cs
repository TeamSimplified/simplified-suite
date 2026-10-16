using SimplifiedDatabaseMerger.Core;

namespace SimplifiedDatabaseMerger;

/// <summary>
/// One change an edited database made, as its tab shows it: what the record
/// was, what this database has instead, and what becomes of it in the merge.
/// </summary>
public sealed class ChangeRow(Change change, Keep side, MergePlan plan)
{
    public string KeyText { get; } = change.Key.ToString("X16");

    public string KindText { get; } = change.Kind.ToString();

    public string CleanText { get; } = DatabaseMerge.Describe(change.Clean);

    public string EditedText { get; } = DatabaseMerge.Describe(change.Edited);

    /// <summary>
    /// Whether this change is in the merged file. Read each time rather than
    /// kept, because a conflict's answer is whatever was last picked on the
    /// Conflicts tab.
    /// </summary>
    public string MergeText => plan.ConflictOn(change.Key) is not { } conflict
        ? "Goes in"
        : conflict.Keep == side ? "Kept (conflict)" : "Not kept (conflict)";
}
