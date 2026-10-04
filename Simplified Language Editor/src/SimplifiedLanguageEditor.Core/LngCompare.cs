using EgoEngineLibrary.Language;

namespace SimplifiedLanguageEditor.Core;

/// <summary>How one language file differs from another.</summary>
/// <param name="Changed">Keys both files have, with different text.</param>
/// <param name="Added">Keys only the first file has.</param>
public sealed record Difference(int Changed, int Added)
{
    /// <summary>How many entries an export would hold.</summary>
    public int Total => Changed + Added;
}

/// <summary>
/// What one file says that another does not: the entries whose text has been
/// changed, and the entries that are new.
///
/// This is how a translation or a rewrite is turned into something installable.
/// A whole language file is six megabytes and fifty-six thousand lines, nearly
/// all of them the game's own; what somebody actually did to it is usually a few
/// dozen. Exporting only those makes a mod that still applies after the game is
/// patched, because every line nobody touched comes from whatever the game ships
/// that week rather than from a copy taken months ago.
///
/// Keys that only the other file has are not in it. They are not something this
/// file says ~ they are something it is silent about, and a file of absences is
/// not a thing the format can hold.
/// </summary>
public static class LngCompare
{
    /// <summary>
    /// Count what <paramref name="mine"/> says differently from
    /// <paramref name="theirs"/>, without building anything.
    ///
    /// So the question "is there anything to export?" can be answered before
    /// somebody is asked where to put it.
    /// </summary>
    public static Difference Measure(LngFile mine, LngFile theirs)
    {
        var baseline = theirs.hsht.Entries
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        var changed = 0;
        var added = 0;

        foreach (var entry in mine.hsht.Entries)
        {
            if (!baseline.TryGetValue(entry.Key, out var was)) added++;
            else if (!string.Equals(was, entry.Value, StringComparison.Ordinal)) changed++;
        }

        return new Difference(changed, added);
    }

    /// <summary>
    /// Build a language file holding only those entries, with
    /// <paramref name="mine"/>'s text in them.
    ///
    /// The library's own, and the arguments are the way round they look wrong:
    /// <c>a.GetDifferences(b)</c> returns what <c>b</c> has that <c>a</c> does
    /// not, carrying <c>a</c>'s header. So the baseline is asked, and what comes
    /// back is this file's work in the game's own file shape.
    /// </summary>
    public static LngFile Extract(LngFile mine, LngFile theirs) => theirs.GetDifferences(mine);
}
