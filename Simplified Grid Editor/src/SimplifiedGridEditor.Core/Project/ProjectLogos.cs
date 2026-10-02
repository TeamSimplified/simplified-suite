using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedGridEditor.Core.Project;

/// <summary>One picture a project puts in one of the game's logo slots.</summary>
/// <param name="Kind">Where in the game it is drawn.</param>
/// <param name="Slice">Which slice of that kind's arrays it goes into.</param>
/// <param name="PngPath">The picture itself, inside the project.</param>
public sealed record StoredLogo(LogoKind Kind, int Slice, string PngPath)
{
    /// <summary>The edit that puts this picture into an archive.</summary>
    public LogoEdit AsEdit() => new(Kind, Slice, PngPath);

    /// <summary>Which team's slot this is, and on which grid.</summary>
    public (Season Season, string TeamId)? Owner => LogoSlices.WhoseSlice(Kind.Id, Slice);
}

/// <summary>
/// The logos a project carries, as the pictures themselves.
///
/// A project used to carry the game's branding archive with the chosen pictures
/// already baked into it. That cost three megabytes of game file per project,
/// and worse, it kept the result instead of the choice: nothing afterwards
/// could say which slices had been replaced, put one back, or hand the picture
/// to a package built against a newer game.
///
/// So a project carries the PNGs, and they go into the archive at packaging
/// time — the same way its database and text changes go into the game's own
/// files. What is stored is what the user chose.
///
/// The file name is the record. <c>badges-6.png</c> is slice 6 of the badge
/// arrays, which <see cref="LogoSlices"/> can name as McLaren's 2025 badge, so
/// there is nothing else to write down and no manifest to disagree with the
/// folder.
/// </summary>
public static class ProjectLogos
{
    /// <summary>What a stored picture is called.</summary>
    public static string FileNameFor(string kindId, int slice) => $"{kindId}-{slice}.png";

    /// <summary>
    /// Pull a stored name apart.
    ///
    /// False for anything else in the folder, which is then left alone rather
    /// than guessed at — a project is a zip somebody can open and put things
    /// in.
    /// </summary>
    public static bool TryRead(string fileName, out LogoKind? kind, out int slice)
    {
        kind = null;
        slice = -1;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var at = stem.LastIndexOf('-');
        if (at <= 0 || !int.TryParse(stem[(at + 1)..], out var parsed) || parsed < 0) return false;

        kind = TeamLogos.Kinds.FirstOrDefault(k => k.Id == stem[..at]);
        if (kind is null) return false;

        slice = parsed;
        return true;
    }

    /// <summary>Every picture the project carries.</summary>
    public static IReadOnlyList<StoredLogo> Read(GridProject project)
    {
        var folder = project.LogosFolder;
        if (!Directory.Exists(folder)) return [];

        var logos = new List<StoredLogo>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            if (TryRead(Path.GetFileName(file), out var kind, out var slice))
                logos.Add(new StoredLogo(kind!, slice, file));

        return logos;
    }

    /// <summary>
    /// Make the folder match what the Teams tab is holding: the chosen pictures
    /// copied in, and the ones no longer chosen removed.
    ///
    /// Removing them is the point of storing pictures rather than a baked
    /// archive. Putting a logo back to the game's own used to be impossible
    /// once it had been saved; now it is a file that stops being there.
    /// </summary>
    /// <returns>Anything that could not be stored, to be reported as a note.</returns>
    public static List<string> Write(GridProject project, IReadOnlyList<LogoEdit> edits)
    {
        var problems = new List<string>();
        var folder = project.LogosFolder;
        Directory.CreateDirectory(folder);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edit in edits)
        {
            var name = FileNameFor(edit.Kind.Id, edit.Slice);
            var to = Path.Combine(folder, name);
            keep.Add(name);

            try
            {
                // A reopened project hands back the file it already holds.
                if (string.Equals(Path.GetFullPath(edit.PngPath), Path.GetFullPath(to),
                        StringComparison.OrdinalIgnoreCase)) continue;

                File.Copy(edit.PngPath, to, overwrite: true);
            }
            catch (Exception ex)
            {
                keep.Remove(name);
                problems.Add($"{Path.GetFileName(edit.PngPath)} couldn't be stored: {ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            if (!TryRead(Path.GetFileName(file), out _, out _)) continue;   // not ours to delete

            try { File.Delete(file); }
            catch { /* it will ship a logo nobody asked for; not worth failing a save over */ }
        }

        return problems;
    }

    /// <summary>
    /// Take an older project's logos out of the archive it carried, as
    /// pictures.
    ///
    /// Those projects baked the chosen logos into their own copy of the game's
    /// branding archive, so what they changed is whichever slices of that copy
    /// differ from the game's. Only the slices a team actually sits at are
    /// looked at, because those are the only ones the editor could have
    /// written.
    ///
    /// The recovered picture goes through DDS and texconv, so a compressed
    /// logo is compressed once more on the way out. That is the cost of having
    /// kept the result rather than the choice, and it is paid once.
    /// </summary>
    /// <returns>
    /// How many pictures came out, and how many would not. A failure count
    /// above zero is why the archive is kept rather than dropped: without
    /// texconv nothing can come out of it, and a later open can try again.
    /// </returns>
    public static (int Recovered, int Failed) Recover(
        GridProject project, string pristineBrandingPath, TexconvRunner runner)
    {
        var stored = project.BrandingErpPath;
        if (!File.Exists(stored) || !File.Exists(pristineBrandingPath)) return (0, 0);

        var mine = ErpFileIO.Load(stored);
        var theirs = ErpFileIO.Load(pristineBrandingPath);

        var folder = project.LogosFolder;
        Directory.CreateDirectory(folder);

        var recovered = 0;
        var failed = 0;

        foreach (var kind in TeamLogos.Kinds)
        {
            // One of a kind's arrays is enough: a chosen picture was written
            // into every array the kind has, so they all carry the same change.
            var texarray = kind.Texarrays[0];

            foreach (var slice in Slices(kind))
            {
                var changed = LogoBaker.SliceBytes(mine, texarray, slice);
                var original = LogoBaker.SliceBytes(theirs, texarray, slice);

                if (changed is null || original is null || changed.AsSpan().SequenceEqual(original)) continue;

                try
                {
                    if (LogoBaker.ExportSlice(mine, texarray, slice, folder, runner,
                            FileNameFor(kind.Id, slice)) is not null) recovered++;
                    else failed++;
                }
                catch
                {
                    // One logo that won't come out shouldn't stop a project from
                    // opening. It is counted, and the caller keeps the archive.
                    failed++;
                }
            }
        }

        return (recovered, failed);
    }

    /// <summary>Every slice of a kind some team sits at, on either grid.</summary>
    private static IEnumerable<int> Slices(LogoKind kind) =>
        new[] { Season.Y2025, Season.Y2026 }
            .SelectMany(season => TeamGrid.For(season)
                .Select(team => LogoSlices.SliceFor(kind, season, team.Id)))
            .OfType<int>()
            .Distinct();
}
