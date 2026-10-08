using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Lng;

namespace SimplifiedSponsorEditor.Core.Sponsors;

/// <summary>What a replaced sponsor should be called in the game, and what it says.</summary>
/// <param name="Sponsor">The game sponsor being replaced, by its catalogue name.</param>
public sealed record SponsorText(string Sponsor, string Name, string Bio);

/// <summary>
/// The sponsor's name and description, as the game's language files hold them.
///
/// Replacing the artwork without the text leaves the game calling somebody's
/// logo by the name of the sponsor it replaced, which is the one thing a decal
/// cannot say for itself.
///
/// Two keys per sponsor: <c>lng_sponsor_&lt;name&gt;</c> and the same with
/// <c>_bio</c>. Measured against the bundled archives, 102 of the 109 sponsors
/// with decals are named by a key spelled exactly like the sponsor the
/// catalogue reads off their textures, which is what makes this a rule rather
/// than a table. The seven that are not are below.
///
/// Only 21 of the 109 ship a bio. The other 88 get the key added, which costs a
/// rehash of the file and nothing else — whether the game shows a description
/// for a sponsor that never had one is the game's business, and an absent key
/// is certainly not shown.
/// </summary>
public static class SponsorLanguage
{
    /// <summary>The family every sponsor's text is filed under.</summary>
    private const string Prefix = "lng_sponsor_";

    /// <summary>What the game's language files are called.</summary>
    public const string FilePattern = "language_*.lng";

    /// <summary>
    /// Sponsors whose language key is spelled differently from the name their
    /// textures carry.
    ///
    /// The four ranked decals, which the game files under a number rather than
    /// a division: <c>lng_sponsor_ranked01</c> through <c>04</c> read "Bronze",
    /// "Silver", "Gold" and "Elite Division Decal", in that order, and the
    /// textures are ranked_division1 through 4.
    /// </summary>
    private static readonly Dictionary<string, string> Spelled = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ranked_division1"] = "ranked01",
        ["ranked_division2"] = "ranked02",
        ["ranked_division3"] = "ranked03",
        ["ranked_division4"] = "ranked04",
    };

    /// <summary>
    /// Sponsors the sponsor family does not name at all, and the key that names
    /// them instead.
    ///
    /// Carbonis is named by <c>lng_decal07</c>, one of a numbered list of 31
    /// the game keeps alongside this one — found by looking for a key whose
    /// value is already "Carbonis", not by guessing at the number. It has no
    /// bio key anywhere, and nothing in the numbered family does.
    /// </summary>
    private static readonly Dictionary<string, string> Elsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        ["carbonis"] = "lng_decal07",
    };

    /// <summary>Where the sponsor's name is written.</summary>
    public static string NameKey(string sponsor) =>
        Elsewhere.TryGetValue(sponsor, out var own) ? own : Prefix + Spell(sponsor);

    /// <summary>
    /// Where its description is written, or null for a sponsor the game names
    /// from outside this family — there is no description beside such a name,
    /// and inventing a key next to one is inventing a key.
    /// </summary>
    public static string? BioKey(string sponsor) =>
        Elsewhere.ContainsKey(sponsor) ? null : Prefix + Spell(sponsor) + "_bio";

    private static string Spell(string sponsor) =>
        Spelled.TryGetValue(sponsor, out var spelled) ? spelled : sponsor;

    /// <summary>
    /// Where language files sit inside a package. The suite's one spelling of
    /// it — note that the folder really is "localisatioa" — taken from the app
    /// that worked it out rather than written down again here.
    /// </summary>
    public static string PackagePath(string fileName) =>
        string.Join('/', GameFiles.PackageLocalisationPath.Append(fileName));

    /// <summary>
    /// What to change in one language file, and what could not be changed.
    ///
    /// The same text in every language. We have one of each, and a sponsor
    /// called something in English and the old name in German would be worse
    /// than one called the same thing everywhere.
    /// </summary>
    /// <param name="notes">
    /// Added to, not replaced. A name with nowhere to go is worth saying once —
    /// so the caller passes the same list for every language and the eleventh
    /// file does not repeat what the first already said.
    /// </param>
    public static Dictionary<string, string> EditsFor(
        LngFile file, IEnumerable<SponsorText> text, ICollection<string>? notes = null)
    {
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var said in text)
        {
            var name = said.Name.Trim();
            var bio = said.Bio.Trim();

            if (name.Length > 0)
            {
                var key = NameKey(said.Sponsor);

                // Only a key the file already has. The game looks text up by a
                // key of its own choosing, and one we added for a sponsor it
                // names some other way would sit in the file unread while the
                // old name went on showing.
                if (file.Entries.ContainsKey(key)) edits[key] = name;
                else Say(notes, $"The game has no name for {said.Sponsor}, so it keeps calling "
                    + "that sponsor what it did. The artwork is still replaced.");
            }

            if (bio.Length == 0) continue;

            if (BioKey(said.Sponsor) is not { } bioKey)
            {
                Say(notes, $"{said.Sponsor} has no description in the game, so its bio was not used.");
                continue;
            }

            edits[bioKey] = bio;
        }

        return edits;
    }

    private static void Say(ICollection<string>? notes, string note)
    {
        if (notes is not null && !notes.Contains(note)) notes.Add(note);
    }

    /// <summary>
    /// Every language file, edited, written under <paramref name="stagingRoot"/>
    /// where the package expects them. Returns how many were written.
    ///
    /// Rebuilt only when a key is being added, which is the common case for a
    /// bio and never the case for a name: adding changes which bucket every
    /// entry hashes into, where editing a value leaves the hash table pointing
    /// exactly where it did.
    /// </summary>
    public static int WriteInto(string languageDir, string stagingRoot,
                                IReadOnlyCollection<SponsorText> text,
                                ICollection<string>? notes = null)
    {
        if (text.Count == 0 || !Directory.Exists(languageDir)) return 0;

        var into = Path.Combine(stagingRoot,
            Path.Combine(GameFiles.PackageLocalisationPath));

        Directory.CreateDirectory(into);

        var written = 0;

        foreach (var path in Directory.EnumerateFiles(languageDir, FilePattern).OrderBy(f => f))
        {
            var file = LngFile.Load(path);
            var edits = EditsFor(file, text, notes);
            if (edits.Count == 0) continue;

            var adding = edits.Keys.Any(key => !file.Entries.ContainsKey(key));

            byte[] bytes;
            if (adding)
            {
                var all = new Dictionary<string, string>(file.Entries, StringComparer.Ordinal);
                foreach (var (key, value) in edits) all[key] = value;
                bytes = file.BuildRebuiltData(all);
            }
            else
            {
                bytes = file.BuildSaveData(edits);
            }

            File.WriteAllBytes(Path.Combine(into, Path.GetFileName(path)), bytes);
            written++;
        }

        return written;
    }
}
