using System.Text.Json;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Util;
using Simplified.Shared;

namespace SimplifiedTeamEditor.Core;

/// <summary>Whether the base files beside an app still match the game.</summary>
public enum OriginalsState
{
    /// <summary>They are there and they came from the installed build.</summary>
    Current,

    /// <summary>They have never been taken. A fresh install of the app.</summary>
    FirstTime,

    /// <summary>They are from a build the game has since moved on from.</summary>
    GameUpdated,

    /// <summary>No game to take them from, so there is nothing to say.</summary>
    NoGame,
}

/// <summary>
/// The game database and the team-branding texture arrays a project is built
/// from, taken out of the installed game rather than shipped.
///
/// They used to come with the apps, copied from one build and stale from the
/// next patch on - and they are the game's files, which the suite does not
/// distribute. Both can be had from any install: the database is a loose file,
/// and the 23 texture arrays of ui_texarrays_team_branding.erp are resources the
/// game keeps in its own UI archives (flow_frontend, flow_race, flow_team_hq and
/// six more), byte for byte what the bundled archive held. So the app takes
/// them on its first run and again whenever the game is patched, into the same
/// runtime\original the apps have always read, and writes originals.json beside
/// them saying which build they came from - the same arrangement as the
/// language files (Simplified.Language.LanguageFiles).
///
/// A database the guard doesn't recognise is still taken after a game update,
/// since a new build is exactly when the manifest has never seen it. On a build
/// the files were already taken from, it is not: the game hasn't changed, so the
/// database has - a mod - and the copy taken before stays.
/// </summary>
public static class GameOriginals
{
    /// <summary>The database, inside the originals folder.</summary>
    public const string DatabaseFileName = "gamedatabases.2025.bin";

    /// <summary>The team-branding archive, inside the originals folder.</summary>
    public static readonly string BrandingRelPath = Path.Combine("ui", "ui_texarrays_team_branding.erp");

    private const string TextureArrayPrefix = "eaid://ui_package/art/texture_arrays/overrides.uidb/overrides.";
    private static readonly string UiPackageRelPath = Path.Combine("2025_asset_groups", "ui_package");

    /// <summary>
    /// The texture arrays the branding archive holds, in its order, and the game
    /// archive under ui_package each is taken from. Several are in more archives
    /// than one, identical in each; this is the first. One that has moved after
    /// a patch is looked for in every UI archive.
    /// </summary>
    private static readonly (string Name, string Archive)[] TextureArrays =
    {
        ("team_logos.driver_career_2024_team_select", "common_front_end.erp"),
        ("teams.badges_broadcast", "common_front_end.erp"),
        ("challenge_career.team_icons", "flow_career_hub.erp"),
        ("teams.driver_select_team_logos", "flow_career_hub.erp"),
        ("teams.team_contract_logo", "flow_career_hub.erp"),
        ("driver_career_rewards.recognition.team_icons", "flow_career_rewards.erp"),
        ("teams.fanzone_expressions_large", "flow_f1_world.erp"),
        ("teams.fanzone_room", "flow_f1_world.erp"),
        ("team_logos.f1_2023", "flow_frontend.erp"),
        ("team_logos.f1_2026", "flow_frontend.erp"),
        ("team_logos.f2_2024", "flow_frontend.erp"),
        ("team_logos.f2_2024_showroom", "flow_frontend.erp"),
        ("team_logos.f2_2025", "flow_frontend.erp"),
        ("team_logos.f2_2025_showroom", "flow_frontend.erp"),
        ("teams.mp_team_select_logos", "flow_frontend.erp"),
        ("teams.fanzone_expressions", "flow_persistent.erp"),
        ("broadcast_ui_generic.team", "flow_race.erp"),
        ("osd.timing_tower.team_badges_broadcast", "flow_race.erp"),
        ("osd.timing_tower.team_badges_hud", "flow_race.erp"),
        ("broadcast_ui_generic.grid_team_logos", "flow_race_intro.erp"),
        ("team_hq.contract_holding_screen_bgs", "flow_team_hq.erp"),
        ("team_hq.contract_logos_mid", "flow_team_hq.erp"),
        ("team_hq.contract_negotiation_logos", "flow_team_hq.erp"),
    };

    /// <summary>
    /// Whether the files in <paramref name="originalsDir"/> came from the
    /// installed game. Asked at startup, so it reads a small note and two file
    /// names and nothing else.
    /// </summary>
    public static OriginalsState Check(string? gameDir, string originalsDir)
    {
        var installed = Stamp(gameDir);
        if (installed is null) return OriginalsState.NoGame;

        if (!File.Exists(Path.Combine(originalsDir, DatabaseFileName)) ||
            !File.Exists(Path.Combine(originalsDir, BrandingRelPath)))
            return OriginalsState.FirstTime;

        var info = OriginalsInfo.Read(originalsDir);
        if (info is null || info.Build.Length == 0) return OriginalsState.FirstTime;
        return info.Build == installed ? OriginalsState.Current : OriginalsState.GameUpdated;
    }

    /// <summary>
    /// Take both out of the game into <paramref name="originalsDir"/>, replacing
    /// what is there, and write the note saying which build they came from.
    /// Returns anything worth telling the person - nothing, ordinarily.
    /// </summary>
    public static IReadOnlyList<string> Refresh(string? gameDir, string originalsDir,
                                                IProgress<string>? progress = null,
                                                CancellationToken cancel = default)
    {
        var stamp = Stamp(gameDir)
                    ?? throw new InvalidOperationException(
                        "The game folder isn't set, or has no game exe at the top of it. The team database " +
                        "and logos are taken from your game - there is nowhere else to get them.");
        var notes = new List<string>();

        // Into a folder of their own first, then swapped in: a database from one
        // build beside logos from another would look fine and be wrong.
        var staging = Path.Combine(originalsDir, ".incoming");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Taking the team database…");
            var database = TakeDatabase(gameDir!, originalsDir, stamp, notes);
            if (database is not null) File.Copy(database, Path.Combine(staging, DatabaseFileName));
            cancel.ThrowIfCancellationRequested();

            progress?.Report("Taking the team logos…");
            var branding = Path.Combine(staging, BrandingRelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(branding)!);
            BuildBranding(gameDir!, branding, progress, cancel);

            progress?.Report("Putting the files in place…");
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(originalsDir, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            OriginalsInfo.Write(originalsDir, GameVersion.ReadName(gameDir) ?? "", GameVersion.Read(gameDir) ?? "", stamp);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* left for next time */ }
        }
        return notes;
    }

    /// <summary>
    /// The game's database to take, or null to keep the copy already taken.
    /// See the class note for when an unrecognised one is taken anyway.
    /// </summary>
    private static string? TakeDatabase(string gameDir, string originalsDir, string stamp, List<string> notes)
    {
        var check = GameFileGuard.Check(gameDir, GameFileGuard.DatabasePath);
        if (check.Verdict == GameFileVerdict.Missing)
            throw new FileNotFoundException(
                "There's no team database under the game folder. Is that the install root?", check.FullPath);
        if (check.Verdict != GameFileVerdict.Unrecognised) return check.FullPath;

        var have = File.Exists(Path.Combine(originalsDir, DatabaseFileName));
        var sameBuild = OriginalsInfo.Read(originalsDir)?.Build == stamp;
        if (have && sameBuild)
        {
            notes.Add("Your game's database has changed since the app took it, on the same game build - " +
                      "a mod is the likeliest reason - so the copy taken before is kept.");
            return null;
        }
        notes.Add("Your game's database isn't one this release has on record, so it was taken as it is. " +
                  "If a database mod is installed, verify the game's files in Steam and reopen the app.");
        return check.FullPath;
    }

    /// <summary>The 23 texture arrays, out of the game's UI archives, into one archive.</summary>
    private static void BuildBranding(string gameDir, string target, IProgress<string>? progress,
                                      CancellationToken cancel)
    {
        var uiPackage = Path.Combine(gameDir, UiPackageRelPath);
        if (!Directory.Exists(uiPackage))
            throw new DirectoryNotFoundException($"The game's UI archives aren't where they should be: {uiPackage}");

        var opened = new Dictionary<string, ErpFile>(StringComparer.OrdinalIgnoreCase);
        ErpFile Open(string path) =>
            opened.TryGetValue(path, out var erp) ? erp : opened[path] = ErpFileIO.Load(path);

        // In the order the game's own archives hold them: per source archive,
        // every surface (.image) and then every view of it.
        var order = TextureArrays
            .GroupBy(t => t.Archive)
            .SelectMany(g => g.Select(t => (Id: TextureArrayPrefix + t.Name + ".texarray.image", t.Archive))
                              .Concat(g.Select(t => (Id: TextureArrayPrefix + t.Name + ".texarray", t.Archive))));

        Dictionary<string, string>? everywhere = null;   // identifier -> archive, built only if needed
        var branding = new ErpFile();
        foreach (var (id, archive) in order)
        {
            cancel.ThrowIfCancellationRequested();
            var source = Find(Path.Combine(uiPackage, archive), id, Open);
            if (source is null)
            {
                everywhere ??= IndexAll(uiPackage, progress);
                if (everywhere.TryGetValue(id, out var elsewhere)) source = Find(elsewhere, id, Open);
            }
            if (source is null)
                throw new InvalidDataException(
                    $"The game no longer has {id.Substring(TextureArrayPrefix.Length)} in its UI archives, " +
                    "so the team logos can't be taken from this build.");

            if (branding.Resources.Count == 0) branding.Version = source.ParentFile.Version;
            branding.Resources.Add(source.Clone(branding));
        }
        ErpFileIO.Save(branding, target);
    }

    private static ErpResource? Find(string archive, string id, Func<string, ErpFile> open) =>
        File.Exists(archive)
            ? open(archive).Resources.FirstOrDefault(r => string.Equals(r.Identifier, id, StringComparison.Ordinal))
            : null;

    /// <summary>Every texture array in every UI archive, by identifier, read from the tables alone.</summary>
    private static Dictionary<string, string> IndexAll(string uiPackage, IProgress<string>? progress)
    {
        progress?.Report("Looking for the team logos in the game's UI…");
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(uiPackage, "*.erp", SearchOption.AllDirectories))
        {
            if (ErpIndex.TryRead(file) is not { } index) continue;
            foreach (var entry in index.Resources)
                if (entry.Identifier.StartsWith(TextureArrayPrefix, StringComparison.Ordinal))
                    all.TryAdd(entry.Identifier, file);
        }
        return all;
    }

    /// <summary>
    /// What identifies a build of the game: its version and its exe's length,
    /// as for the language files. Null without a game.
    /// </summary>
    private static string? Stamp(string? gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
            var exe = Directory.EnumerateFiles(gameDir, "F1_*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe is null) return null;
            return $"{GameVersion.Read(gameDir)}-{new FileInfo(exe).Length}";
        }
        catch
        {
            return null;
        }
    }
}
