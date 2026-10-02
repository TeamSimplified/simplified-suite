using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;
using SimplifiedTeamEditor.Core.Teams;

namespace SimplifiedTeamEditor.Core.Logos;

/// <summary>One team's logo, found inside an archive somebody else built.</summary>
/// <param name="Kind">Where in the game it is drawn.</param>
/// <param name="Slice">The slice it was found at.</param>
/// <param name="Season">Which grid that slice belongs to.</param>
/// <param name="TeamId">Whose slot it is.</param>
/// <param name="Texarray">
/// The array it was read from. A kind can have three, and an archive need only
/// carry one of them for the logo to be in there.
/// </param>
public sealed record FoundLogo(
    LogoKind Kind, int Slice, Season Season, string TeamId, string Texarray)
{
    /// <summary>The team, where this is one of the grids the app knows.</summary>
    public TeamDefinition? Team =>
        TeamGrid.For(Season).FirstOrDefault(t => t.Id == TeamId);
}

/// <summary>
/// An archive somebody hands us, read for the team logos it carries.
///
/// The game's logo arrays are not private to this app: a mod's own branding
/// archive has them, and so does a UI shader package a launcher has already
/// merged one into. Anything holding those arrays can therefore be read for
/// the logos in it — which is the only way to take a logo out of a mod that
/// ships as an archive rather than as the pictures it was built from.
///
/// What counts as "carries a logo" is a slice that differs from the game's own
/// copy of the same array. That is the same test an older project's conversion
/// uses, and for the same reason: an archive says what is in it, not what
/// somebody changed, so the change is the difference.
///
/// Only the slices a team actually sits at are looked at. A slice that differs
/// and belongs to nobody is counted and left alone — it cannot be filed under a
/// team, and guessing would put somebody else's picture in a slot.
/// </summary>
public sealed class LogoSource
{
    private readonly ErpFile _archive;

    private LogoSource(ErpFile archive, string path,
                       List<FoundLogo> logos, List<string> arrays, int unclaimed)
    {
        _archive = archive;
        Path = path;
        Logos = logos;
        Arrays = arrays;
        Unclaimed = unclaimed;
    }

    /// <summary>The archive this was read from.</summary>
    public string Path { get; }

    /// <summary>Every team logo in it, in the order the game lays the arrays out.</summary>
    public IReadOnlyList<FoundLogo> Logos { get; }

    /// <summary>
    /// Which of the game's logo arrays it holds, whether or not anything in
    /// them differs. An archive with none of them is the wrong archive, and
    /// that is worth saying differently from one that simply matches the game.
    /// </summary>
    public IReadOnlyList<string> Arrays { get; }

    /// <summary>How many differing slices belong to no team on either grid.</summary>
    public int Unclaimed { get; }

    /// <summary>Read an archive and work out what is in it.</summary>
    /// <param name="erpPath">The archive to read.</param>
    /// <param name="pristineBrandingPath">The game's own copy, to compare against.</param>
    public static LogoSource Read(string erpPath, string pristineBrandingPath)
    {
        var archive = ErpFileIO.Load(erpPath);
        var pristine = ErpFileIO.Load(pristineBrandingPath);

        var logos = new List<FoundLogo>();
        var arrays = new List<string>();
        var unclaimed = 0;

        foreach (var kind in TeamLogos.Kinds)
        {
            // Whichever of the kind's arrays this archive has. One is enough:
            // a logo is written into every array of its kind, so they carry the
            // same picture, and an overlay often keeps only one.
            var texarray = kind.Texarrays.FirstOrDefault(
                t => TeamLogos.ResourceFor(archive, t) is not null);
            if (texarray is null) continue;

            arrays.Add(texarray);

            foreach (var slice in Slices(kind))
            {
                var theirs = LogoBaker.SliceBytes(archive, texarray, slice);
                var ours = LogoBaker.SliceBytes(pristine, texarray, slice);

                if (theirs is null || ours is null || theirs.AsSpan().SequenceEqual(ours)) continue;

                if (LogoSlices.WhoseSlice(kind.Id, slice) is { } owner)
                    logos.Add(new FoundLogo(kind, slice, owner.Season, owner.TeamId, texarray));
                else
                    unclaimed++;
            }
        }

        return new LogoSource(archive, erpPath, logos, arrays, unclaimed);
    }

    /// <summary>
    /// One of the found logos, out as a picture that can be chosen like any
    /// other. Null when it will not come out, which leaves the slot alone.
    /// </summary>
    public string? Extract(FoundLogo logo, string outDir, TexconvRunner runner)
        => LogoBaker.ExportSlice(_archive, logo.Texarray, logo.Slice, outDir, runner,
            $"{logo.TeamId}-{logo.Kind.Id}-{logo.Slice}.png");

    /// <summary>
    /// Every slice of a kind some team sits at, plus the ones in between.
    ///
    /// The in-between slices are what Unclaimed counts. The arrays have holes —
    /// the 2025 showroom has no slice 5, Konnersport's starting-grid logo sits
    /// ten past the team before it — and a mod that filled one is worth
    /// mentioning even though nothing here can place it.
    /// </summary>
    private static IEnumerable<int> Slices(LogoKind kind)
    {
        var taken = new[] { Season.Y2025, Season.Y2026 }
            .SelectMany(season => TeamGrid.For(season)
                .Select(team => LogoSlices.SliceFor(kind, season, team.Id)))
            .OfType<int>()
            .ToList();

        return taken.Count == 0 ? [] : Enumerable.Range(0, taken.Max() + 1);
    }
}
