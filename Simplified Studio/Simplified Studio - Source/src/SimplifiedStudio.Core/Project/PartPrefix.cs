using System.Text.RegularExpressions;

namespace SimplifiedStudio.Core.Project;

/// <summary>
/// The <c>00--00--</c> prefix carried by both category folders and part files.
///
/// <code>
///   00--00--front_wing.erp
///   │     └── conflict group: 00 = none, 01-99 = a group
///   └──────── order: 00-99
/// </code>
///
/// Order drives display <em>and</em> merge sequence — folders first, then parts
/// within a folder — so the number that decides where something appears also
/// decides which part wins when two touch the same resource. Nothing edits it
/// by hand: rows are dragged into place and the app renumbers, which is why the
/// order half is hidden even in Manage ERPs.
///
/// Conflict groups are scoped, which is the point of separating them from the
/// order: two parts are alternatives only when they share a parent folder
/// <em>and</em> a non-zero group, so each folder gets its own ninety-nine
/// groups rather than sharing one set across the project. Folders conflict with
/// other folders the same way, by their own group.
///
/// Anything without a prefix sorts last and belongs to no group.
///
/// Note this is not the pre-1.3 <c>000--</c> scheme, where the first digit was
/// the conflict group and the whole number was the order. A file written then
/// means something different read now — see <c>ProjectStore</c>'s migration.
/// </summary>
public static class PartPrefix
{
    /// <summary>Matches the prefix at the start of a folder or file name.</summary>
    public static readonly Regex Pattern = new(@"^(\d{2})--(\d{2})--", RegexOptions.Compiled);

    /// <summary>Matches just the order half, for stripping it on its own.</summary>
    private static readonly Regex OrderOnly = new(@"^(\d{2})--", RegexOptions.Compiled);

    /// <summary>Sorts after everything that carries a prefix.</summary>
    public const int NoOrder = int.MaxValue;

    /// <summary>Order 00-99, or <see cref="NoOrder"/> when unprefixed.</summary>
    public static int OrderOf(string name)
    {
        var m = Pattern.Match(name ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : NoOrder;
    }

    /// <summary>
    /// Conflict group 1-99, or null for none. <c>00</c> is "no group" rather
    /// than group zero — otherwise every unnumbered part would become mutually
    /// exclusive with every other.
    /// </summary>
    public static int? ConflictOf(string name)
    {
        var m = Pattern.Match(name ?? "");
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var n)) return null;
        return n == 0 ? null : n;
    }

    /// <summary>
    /// The name with both halves removed — what the user sees everywhere except
    /// Manage ERPs.
    /// </summary>
    public static string Display(string name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        var m = Pattern.Match(name);
        return m.Success ? name[m.Length..] : name;
    }

    /// <summary>
    /// The name with only the order removed, leaving the conflict group visible:
    /// <c>03--01--front.erp</c> reads as <c>01--front.erp</c>. This is the
    /// Manage ERPs form — order is the app's business, since rows are dragged
    /// rather than numbered, but the group is the user's.
    /// </summary>
    public static string DisplayWithGroup(string name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        var m = Pattern.Match(name);
        return m.Success ? name[OrderOnly.Match(name).Length..] : name;
    }

    /// <summary>Build a prefix. Both halves are clamped to 00-99.</summary>
    public static string Format(int order, int conflict) =>
        $"{Math.Clamp(order, 0, 99):00}--{Math.Clamp(conflict, 0, 99):00}--";

    /// <summary>
    /// Re-prefix a name, replacing any prefix it already has. Passing the
    /// display name works just as well as passing the prefixed one.
    /// </summary>
    public static string Apply(string name, int order, int conflict) =>
        Format(order, conflict) + Display(name ?? "");

    /// <summary>
    /// Re-number only the order, keeping whatever group the name already
    /// carries. This is what a drag-and-drop reorder does — moving a row must
    /// never change what it's an alternative to.
    /// </summary>
    public static string ApplyOrder(string name, int order) =>
        Apply(name, order, ConflictOf(name) ?? 0);

    /// <summary>True when the name carries a well-formed prefix.</summary>
    public static bool Has(string name) => Pattern.IsMatch(name ?? "");

    /// <summary>
    /// Order first, then name — the sort used for folders and for parts within
    /// a folder. Unprefixed entries fall to the bottom and sort alphabetically
    /// among themselves.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var byOrder = OrderOf(a).CompareTo(OrderOf(b));
        return byOrder != 0
            ? byOrder
            : string.Compare(Display(a), Display(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="Compare"/> as a comparer, for sorting collections.</summary>
    public static readonly IComparer<string> Comparer =
        System.Collections.Generic.Comparer<string>.Create(Compare);
}
