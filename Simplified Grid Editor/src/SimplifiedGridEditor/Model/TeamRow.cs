using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedGridEditor.Model;

/// <summary>
/// One team's row in the editor: its names and colours as they read now, and
/// what they were when the files were read.
///
/// The originals are kept per row so "reset" needs nothing from disk, and so
/// the modified marker can be decided by comparison rather than by a flag that
/// has to be maintained on every keystroke.
/// </summary>
public sealed class TeamRow
{
    public required TeamDefinition Team { get; init; }

    // ── Names, from the language file ─────────────────────────────────
    public string Full { get; set; } = "";
    public string Short { get; set; } = "";
    public string SuperShort { get; set; } = "";

    public required string OriginalFull { get; set; }
    public required string OriginalShort { get; set; }
    public required string OriginalSuperShort { get; set; }

    // ── Colours, from the game database ───────────────────────────────

    /// <summary>
    /// The team's index in the database, or null when it isn't in there. A
    /// team can have names and no colours: the language file and the database
    /// are separate, and only the database knows about entrants.
    /// </summary>
    public int? DatabaseIndex { get; set; }

    public Dictionary<ColourField, string> Colours { get; } = new();
    public Dictionary<ColourField, string> OriginalColours { get; } = new();

    // ── Logos, chosen from disk ───────────────────────────────────────

    /// <summary>
    /// Logo kind to the PNG standing in for the game's own. Absent means the
    /// game's picture is kept, which is why there is no "original" to compare
    /// against here as there is for names and colours.
    /// </summary>
    public Dictionary<string, string> Logos { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The same as the project has it, so a logo the project already carries
    /// does not read as an unsaved change. Names and colours have had one of
    /// these all along; logos only needed one once a project started storing
    /// the picture rather than an archive with the picture already in it.
    /// </summary>
    public Dictionary<string, string> OriginalLogos { get; } = new(StringComparer.Ordinal);

    public bool LogosModified =>
        Logos.Count != OriginalLogos.Count
        || Logos.Any(l => OriginalLogos.GetValueOrDefault(l.Key) != l.Value);

    public bool NamesModified =>
        Full != OriginalFull || Short != OriginalShort || SuperShort != OriginalSuperShort;

    public bool ColoursModified =>
        Colours.Any(c => OriginalColours.GetValueOrDefault(c.Key) != c.Value);

    public bool IsModified => NamesModified || ColoursModified || LogosModified;

    public void Reset()
    {
        ResetNames();
        ResetColours();
        ResetLogos();
    }

    /// <summary>
    /// Take what the row holds as the project's own.
    ///
    /// "Modified" here means "differs from the project", so a save is the
    /// moment the two agree again. Without this the window goes on saying
    /// there are unsaved changes after every save.
    /// </summary>
    public void Rebaseline()
    {
        OriginalFull = Full;
        OriginalShort = Short;
        OriginalSuperShort = SuperShort;

        Copy(Colours, OriginalColours);
        Copy(Logos, OriginalLogos);
    }

    private static void Copy<TKey, TValue>(Dictionary<TKey, TValue> from, Dictionary<TKey, TValue> to)
        where TKey : notnull
    {
        to.Clear();
        foreach (var (key, value) in from) to[key] = value;
    }

    public void ResetNames()
    {
        Full = OriginalFull;
        Short = OriginalShort;
        SuperShort = OriginalSuperShort;
    }

    public void ResetColours()
    {
        foreach (var field in OriginalColours.Keys) Colours[field] = OriginalColours[field];
    }

    /// <summary>
    /// Back to the pictures the project carries. Not the game's own: that is
    /// what the reset beside a single logo in the picker does, and it is an
    /// edit rather than the undoing of one.
    /// </summary>
    public void ResetLogos() => Copy(OriginalLogos, Logos);

    public void ResetColour(ColourField field)
    {
        if (OriginalColours.TryGetValue(field, out var original)) Colours[field] = original;
    }

    public bool IsColourModified(ColourField field)
        => Colours.TryGetValue(field, out var now) && OriginalColours.GetValueOrDefault(field) != now;

    public TeamNames ToNames() => new()
    {
        Team = Team,
        Full = Full,
        Short = Short,
        SuperShort = SuperShort,
    };

    public static TeamRow From(TeamNames names) => new()
    {
        Team = names.Team,
        Full = names.Full,
        Short = names.Short,
        SuperShort = names.SuperShort,
        OriginalFull = names.Full,
        OriginalShort = names.Short,
        OriginalSuperShort = names.SuperShort,
    };
}
