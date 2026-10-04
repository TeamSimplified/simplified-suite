using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Data.GameDatabases;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;
using SimplifiedDriverSwapper.Core.Drivers;
using SimplifiedGridEditor.Core.Project;
using SimplifiedTeamEditor.Core;
using SimplifiedTeamEditor.Core.Lng;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Packaging;
using SimplifiedTeamEditor.Core.Teams;

// Both halves of this app came with a type called Season, and they mean
// different things: the Team Editor's is which grid, the Driver Swapper's is
// that plus the entrant list and the seats on it. Neither is wrong, so this
// file says which it means every time.
using TeamSeason = SimplifiedTeamEditor.Core.Teams.Season;
using DriverSeason = SimplifiedDriverSwapper.Core.Drivers.Season;

namespace SimplifiedGridEditor.Core.Packaging;

/// <summary>What a package turned out to contain.</summary>
public sealed record GridPackageResult(
    int LanguageFiles,
    bool Database,
    int ShaderPackages,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Writing a project down, and writing it out.
///
/// Two jobs, in that order. <see cref="ApplyToProject"/> turns what the tabs are
/// holding into the project's working files, which <see cref="ProjectStore"/>
/// then reads the project's changes out of; <see cref="Write"/> builds the mod
/// from the installed game plus whichever of those changes were asked for, at
/// the paths the game reads them from — as a zip or as a folder, which hold the
/// same files either way.
///
/// Keeping the two apart is what makes a package reproducible: it ships what
/// was saved, not a second rendering of the same edits.
///
/// The Team Editor does both at once, because it has nowhere to put a working
/// copy. A project is that place.
/// </summary>
public static class GridPackager
{
    /// <summary>
    /// Write every staged edit into the project's files.
    ///
    /// Order matters within the database: seats and drivers first, then 2025
    /// colours, the 2026 repointing, and 2026 colours — the repointing rewrites
    /// fields the colour pass reads.
    /// </summary>
    public static void ApplyToProject(
        GridProject project,
        GridDocument? document,
        KitOptions kit,
        SeasonEdits y2025,
        SeasonEdits y2026,
        IReadOnlyList<LogoEdit> logoEdits,
        List<string>? warnings = null)
    {
        ApplyToDatabase(project, document, kit, y2025, y2026);
        ApplyToLanguageFiles(project, y2025, y2026);
        ApplyLogos(project, logoEdits, warnings ?? []);

        // All of it is in the project's files now, so nothing the tabs were
        // holding is still waiting to be written.
        document?.Committed();
    }

    /// <summary>
    /// Store the chosen pictures in the project.
    ///
    /// This is where a logo choice stops being a file path somebody picked and
    /// becomes part of the project. It used to be imported into a copy of the
    /// game's branding archive here, which kept the result rather than the
    /// choice; now the picture itself is kept and the archive is built at
    /// packaging time. See <see cref="ProjectLogos"/>.
    /// </summary>
    private static void ApplyLogos(GridProject project, IReadOnlyList<LogoEdit> edits, List<string> warnings)
        => warnings.AddRange(ProjectLogos.Write(project, edits));

    private static void ApplyToDatabase(
        GridProject project, GridDocument? document, KitOptions kit,
        SeasonEdits y2025, SeasonEdits y2026)
    {
        // The document holds the database the tabs have been reading, so it is
        // the copy the edits belong to. Without one there is nothing to write.
        if (document is null) return;

        var database = document.Database;

        // Seats, and the kit that follows from them.
        document.Seats.Apply(kit);

        // Driver identity fields.
        foreach (var (index, fields) in document.PendingAttributes)
        {
            foreach (var (field, value) in fields)
            {
                var record = database.Find(DriverSeason.DriverPath(index, field));
                if (record is null || record.Text == value) continue;
                record.Text = value;
            }
        }

        // The start screen, the career menu and the featured cars.
        document.Menus.Apply(database);

        ApplyColours(database, TeamSeason.Y2025, y2025.ChangedColours);

        if (y2026.AnyNames)
            Season2026.ApplyReassignment(database, Season2026.LoadReassignment(project.LanguageFolder));

        ApplyColours(database, TeamSeason.Y2026, y2026.ChangedColours);

        // Written through a temporary file and moved into place, so an
        // interrupted write can't leave the project holding half a database.
        database.SaveAtomic(project.DatabasePath);
    }

    private static void ApplyColours(
        GameDatabase database,
        TeamSeason season,
        IReadOnlyDictionary<string, Dictionary<ColourField, string>> changed)
    {
        foreach (var team in TeamGrid.For(season))
        {
            if (!changed.TryGetValue(team.Id, out var fields)) continue;

            var index = TeamColours.FindIndex(database, team.DatabaseId);
            if (index is null) continue;

            foreach (var (field, hex) in fields) TeamColours.Write(database, index.Value, field, hex);
        }
    }

    /// <summary>
    /// Every language gets the same edits. A team renamed in English is renamed
    /// in all of them — the alternative is a game that calls the team one thing
    /// in menus and another in commentary subtitles.
    /// </summary>
    private static void ApplyToLanguageFiles(GridProject project, SeasonEdits y2025, SeasonEdits y2026)
    {
        var edits = TeamNameEditing.BuildEdits(y2025.ChangedNames);

        // Seed every 2026 key with its default and let edits override, so a
        // repointed field can never end up aimed at a key that isn't there.
        var keys2026 = new Dictionary<string, string>(StringComparer.Ordinal);
        if (y2026.AnyNames)
        {
            keys2026 = Season2026.LoadDefaults(project.LanguageFolder);
            foreach (var team in y2026.ChangedNames)
            {
                if (!string.IsNullOrEmpty(team.Full)) keys2026[team.Team.FullKey] = team.Full;
                if (!string.IsNullOrEmpty(team.Short)) keys2026[team.Team.ShortKey] = team.Short;
                if (!string.IsNullOrEmpty(team.SuperShort)) keys2026[team.Team.SuperShortKey] = team.SuperShort;
            }
        }

        if (edits.Count == 0 && keys2026.Count == 0) return;

        foreach (var path in project.LanguageFiles.ToList())
        {
            var lng = LngFile.Load(path);

            byte[] data;
            if (keys2026.Count > 0)
            {
                // Adding keys changes which bucket everything hashes into, so
                // the whole file is rebuilt rather than patched.
                var all = new Dictionary<string, string>(lng.Entries, StringComparer.Ordinal);
                foreach (var (key, value) in edits) all[key] = value;
                foreach (var (key, value) in keys2026) all[key] = value;
                data = lng.BuildRebuiltData(all);
            }
            else
            {
                data = lng.BuildSaveData(edits);
            }

            File.WriteAllBytes(path, data);
        }
    }

    // ── The package ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build the mod at the paths the game reads its files from: the game's own
    /// database and text, with the parts of this project that were asked for
    /// applied on top.
    ///
    /// Everything is ticked by default and that is the usual package. Ticking
    /// less is how a project that holds several pieces of work — this year's
    /// grid while last year's is half finished, the names without the colours —
    /// goes out as one of them.
    /// </summary>
    /// <param name="parts">
    /// Which parts to include. Null means all of them, which is what a caller
    /// that never asks should get.
    /// </param>
    /// <param name="runner">
    /// texconv, which turns the project's logo pictures into the format the
    /// game's arrays hold. Needed only if there are logos to put in.
    /// </param>
    /// <param name="asZip">
    /// True for a .zip, false for a folder of the same files at the same paths.
    /// </param>
    public static GridPackageResult Write(
        string zipPath,
        GridProject project,
        string originalsDir,
        string? gameDir,
        TexconvRunner runner,
        IReadOnlySet<PackagePart>? parts = null,
        bool asZip = true,
        IProgress<string>? say = null)
    {
        var warnings = new List<string>();
        var wanted = parts ?? Enum.GetValues<PackagePart>().ToHashSet();

        // What this project changed, and which part of it each change belongs
        // to. The database underneath is the game's own, so what ships is the
        // game as it is now plus the ticked work.
        var edits = ProjectEdits.Load(project.EditsPath);
        var chosen = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(originalsDir, ProjectStore.DatabaseFileName));

        var keys2026 = Season2026.LoadDefaults(project.LanguageFolder).Keys.ToHashSet(StringComparer.Ordinal);

        // Build beside the destination and move it into place at the end, so a
        // half-written package never appears at the path the user is watching.
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.part");

        var languageFiles = 0;
        var database = false;
        var shaderPackages = 0;

        try
        {
            using (IPackageSink zip = asZip
                       ? new ZipSink(File.Create(temporary))
                       : new FolderSink(Directory.CreateDirectory(temporary).FullName))
            {
                // The text: the game's own, with the ticked name changes in it.
                // Untouched files still ship, because a package replaces every
                // language file it names and a missing one would leave the
                // game reading a mix of two builds.
                say?.Report("Adding your team names…");
                var textWanted = edits.Text
                    .Where(e => wanted.Contains(PackageParts.PartOfText(e.Key, keys2026)))
                    .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

                // A package that renames a team has to carry 2026's own name
                // keys, whichever year was renamed. See KeepSeasonsApart.
                var keepApart = textWanted.Count > 0;
                var names2026 = keepApart
                    ? Season2026.LoadDefaults(project.LanguageFolder)
                    : new Dictionary<string, string>(StringComparer.Ordinal);

                var languageDir = Path.Combine(originalsDir, Simplified.Language.LanguageFiles.FolderName);
                if (Directory.Exists(languageDir))
                    foreach (var path in Directory.EnumerateFiles(languageDir, "language_*.lng").OrderBy(f => f))
                    {
                        byte[] bytes;
                        if (!keepApart)
                        {
                            bytes = File.ReadAllBytes(path);
                        }
                        else
                        {
                            // Per language, because what each one already has
                            // of 2026 differs.
                            var language = LngFile.Load(path);
                            bytes = new ProjectEdits
                            {
                                Text = KeepSeasonsApart(language, names2026, textWanted),
                            }.ApplyTo(language);
                        }

                        zip.Add(string.Join('/', GameFiles.PackageLocalisationPath.Append(Path.GetFileName(path))),
                            bytes);
                        languageFiles++;
                    }

                if (languageFiles == 0)
                    warnings.Add("The game's language files aren't here, so no names were packaged.");

                // The database: the game's own, with the ticked records applied.
                say?.Report("Adding your teams, seats and drivers…");
                if (File.Exists(chosen.Path))
                {
                    var built = GameDatabase.Load(chosen.Path);
                    var map = PackageParts.Read(built, edits, keys2026);

                    var included = new ProjectEdits
                    {
                        Database = edits.Database
                            .Where(e => ulong.TryParse(e.Key, out var key) && wanted.Contains(map.PartOf(key)))
                            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
                    };

                    var left = edits.Database.Count - included.Database.Count;
                    if (left > 0) warnings.Add($"{left} change(s) were left out because their part wasn't ticked.");

                    included.ApplyTo(built);

                    // The other half of keeping the seasons apart: the 2026
                    // teams are pointed at their own keys, which seventeen of
                    // their name fields are not pointed at in a stock game.
                    if (keepApart)
                        Season2026.ApplyReassignment(
                            built, Season2026.LoadReassignment(project.LanguageFolder));

                    zip.Add(string.Join('/', new[] { GameFiles.GameFolderName }.Concat(GameFiles.GameDatabaseRelPath)),
                        built.ToBytes());
                    database = true;
                }
                else
                {
                    warnings.Add("The game's database isn't here, so no teams, seats or drivers were packaged.");
                }

                if (ChooseLogos(project, wanted) is { Count: > 0 } logos)
                {
                    // The slow one: each picture goes through texconv, and both
                    // of the game's shader packages are read, merged into and
                    // written back.
                    say?.Report("Adding your logos…");
                    shaderPackages = WriteShaderPackages(
                        zip, originalsDir, gameDir, logos, runner, warnings);
                }

                zip.Add("mod.serps", Encoding.UTF8.GetBytes(BuildModSerps(project, gameDir)));
            }

            say?.Report("Finishing your mod…");

            if (asZip)
            {
                File.Move(temporary, zipPath, overwrite: true);
            }
            else
            {
                if (Directory.Exists(zipPath)) Directory.Delete(zipPath, recursive: true);
                Directory.Move(temporary, zipPath);
            }
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); } catch { }
            throw;
        }

        return new GridPackageResult(languageFiles, database, shaderPackages, warnings);
    }

    /// <summary>
    /// The pictures the ticked parts asked for.
    ///
    /// Filing a logo under a season is the whole reason the slice is part of
    /// its name: three of the five arrays hold both grids at once, so nothing
    /// but the slice says which year a picture was chosen for.
    /// </summary>
    private static List<StoredLogo> ChooseLogos(GridProject project, IReadOnlySet<PackagePart> wanted)
        => ProjectLogos.Read(project)
            .Where(logo => wanted.Contains(PackageParts.PartOfLogo(logo.Kind.Id, logo.Slice)))
            .ToList();

    /// <summary>
    /// Make sure a renamed 2025 team does not rename the 2026 one with it.
    ///
    /// The game lets the two grids share name keys, and not tidily: a stock
    /// database points seventeen of the 2026 teams' thirty-three name fields
    /// at a 2025 key, and the split is per field rather than per team. Ferrari,
    /// Alpine and Racing Bulls read all three of theirs from 2025; McLaren
    /// reads its title from 2026 and its two short forms from 2025; Mercedes,
    /// Red Bull and Aston Martin the other way about. So renaming Ferrari in
    /// 2025 renames it in 2026 too, and nothing about the 2025 tab says that
    /// is going to happen.
    ///
    /// Every 2026 key therefore goes out with any package that carries a name
    /// change, seeded with what the 2026 tab shows, and the 2026 fields are
    /// pointed at them. Then each grid reads from keys of its own and a rename
    /// stays in the year it was made.
    ///
    /// Only the keys a language hasn't got are seeded, and the package's own
    /// text then overrides whatever is left. The game ships ten of the
    /// thirty-three, Chinese translates every one of its ten, and the defaults
    /// are English — so seeding them over a key the file already has would
    /// throw a translation away to solve a problem that file does not have.
    /// </summary>
    private static Dictionary<string, string> KeepSeasonsApart(
        LngFile language,
        IReadOnlyDictionary<string, string> names2026,
        IReadOnlyDictionary<string, string> text)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in names2026)
            if (!language.Entries.ContainsKey(key)) merged[key] = value;

        foreach (var (key, value) in text) merged[key] = value;

        return merged;
    }

    /// <summary>
    /// Put the chosen logos into the game's branding arrays and merge those
    /// into both shader packages.
    ///
    /// The shader packages come from the game rather than from the project, and
    /// are the one part of a package that cannot be built without an install. A
    /// failure here is reported and the rest of the package still ships —
    /// names, colours and the grid are worth having without logos.
    /// </summary>
    private static int WriteShaderPackages(
        IPackageSink zip, string originalsDir, string? gameDir,
        IReadOnlyList<StoredLogo> logos, TexconvRunner runner, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            warnings.Add("Logos need the game's UI shader packages, and F1 25 wasn't found — they were left out.");
            return 0;
        }

        var branding = ProjectStore.PristineBrandingPath(originalsDir);
        if (!File.Exists(branding))
        {
            warnings.Add("The game's team-branding archive is missing from this build — logos were left out.");
            return 0;
        }

        var shaderDirectory = Path.Combine(gameDir, GameFiles.ShaderFolderName);
        var written = 0;

        // These come out of the user's install, because the game's own shaders
        // have to be in them. Anything already merged into one goes out inside
        // this package, so say so before it does.
        warnings.AddRange(Simplified.Shared.GameFileGuard.Warnings(
            gameDir, GameFiles.ShaderErpNames.Select(Simplified.Shared.GameFileGuard.ShaderPath)));

        try
        {
            // The game's own arrays, stripped to the kinds being changed —
            // merging the rest would replace arrays nobody touched with this
            // build's copies of them, reverting anything else installed — and
            // then the chosen pictures put into their slices.
            //
            // Starting from the game's copy rather than the project's is what
            // makes a package of one season's logos possible: a slice nobody
            // chose a picture for is never written, so it stays the game's.
            var edits = logos.Select(logo => logo.AsEdit()).ToList();
            var (overlay, _) = LogoBaker.BuildOverlay(branding, edits);

            // A picture at the wrong size is the common case — every slice of
            // an array shares one size and format — and it must not take the
            // rest of the package down with it.
            var (imported, skipped) = LogoBaker.ImportAll(overlay, edits, runner);
            warnings.AddRange(skipped);

            // The pair the game expects in any mod that ships a shader package.
            // They ride along in the overlay rather than as a second merge: the
            // overlay carries ui_texarrays_* and these are a surface descriptor
            // and a shadow material, so nothing of either can land on the other.
            Simplified.Shared.SerpsOverlay.AddTo(overlay);

            if (imported == 0)
            {
                warnings.Add("None of the logo pictures could be used, so they were left out.");
                return 0;
            }

            var work = Path.Combine(Path.GetTempPath(), "simplified-grid-editor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                foreach (var name in GameFiles.ShaderErpNames)
                {
                    var source = Path.Combine(shaderDirectory, name);
                    if (!File.Exists(source))
                    {
                        warnings.Add($"{name} isn't in the game's shader folder — that variant was left out.");
                        continue;
                    }

                    var baked = Path.Combine(work, name);
                    LogoBaker.MergeInto(source, overlay, baked);
                    zip.Add($"{GameFiles.GameFolderName}/{GameFiles.ShaderFolderName}/{name}", File.ReadAllBytes(baked));
                    written++;
                }
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Logos could not be baked: {ex.Message}");
        }

        return written;
    }

    /// <summary>
    /// The manifest, at the root where the launcher looks for it. Name, author
    /// and version come from the project rather than from a dialog — the project
    /// already knows them, and asking again is how two answers drift apart.
    /// </summary>
    private static string BuildModSerps(GridProject project, string? gameDir)
    {
        var manifest = new Dictionary<string, string>
        {
            ["mod_name"] = project.Info.Name.Trim().Length > 0 ? project.Info.Name.Trim() : project.DisplayName,
        };
        if (!string.IsNullOrWhiteSpace(project.Info.Author)) manifest["author"] = project.Info.Author.Trim();
        if (!string.IsNullOrWhiteSpace(project.Info.Version)) manifest["version"] = project.Info.Version.Trim();

        // Which game build this was made against, named and numbered the same
        // way Studio does it — so a mod from either app says what it was
        // packaged for, in one spelling. It matters more than it used to: the
        // F2 26 update moved entrant lists around, so a grid mod built before it
        // and one built after are not interchangeable, and the manifest is the
        // only place that difference is written down.
        manifest["game"] = Simplified.Shared.GameVersion.ReadName(gameDir) ?? "";
        manifest["game_version"] = Simplified.Shared.GameVersion.Read(gameDir) ?? "";

        return JsonSerializer.Serialize(manifest, SerpsJson);
    }

    /// <summary>
    /// How the manifest is written — the same options Studio uses, so the two
    /// apps spell a mod.serps identically. The encoder is the reason: the
    /// default one escapes the game's own name to <c>F1® 25</c>, where
    /// this writes <c>F1® 25</c>. Both parse the same; only one is readable.
    /// </summary>
    private static readonly JsonSerializerOptions SerpsJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };


    /// <summary>One line saying what was written, for the status bar.</summary>
    public static string Describe(GridPackageResult result, string zipPath)
    {
        var parts = new List<string>();
        if (result.Database) parts.Add("the database");
        if (result.LanguageFiles > 0) parts.Add($"{result.LanguageFiles} language file(s)");
        if (result.ShaderPackages > 0) parts.Add($"{result.ShaderPackages} shader package(s)");

        var what = parts.Count == 0 ? "the manifest only" : string.Join(", ", parts);
        var line = $"Packaged {Path.GetFileName(zipPath)} — {what}.";
        return result.Warnings.Count == 0 ? line : line + $" {result.Warnings.Count} note(s).";
    }

    /// <summary>
    /// Where a package is being written: a zip, or a folder.
    ///
    /// A mod manager takes a zip and a person dropping files into their game
    /// takes a folder, and the two hold exactly the same thing at exactly the
    /// same paths — so the difference is one interface and the rest of this
    /// file never asks which it is.
    /// </summary>
    private interface IPackageSink : IDisposable
    {
        void Add(string entryPath, byte[] data);
    }

    private sealed class ZipSink(Stream stream) : IPackageSink
    {
        private readonly ZipArchive _zip = new(stream, ZipArchiveMode.Create);

        public void Add(string entryPath, byte[] data)
        {
            var entry = _zip.CreateEntry(entryPath, CompressionLevel.Optimal);
            using var writing = entry.Open();
            writing.Write(data, 0, data.Length);
        }

        public void Dispose()
        {
            _zip.Dispose();
            stream.Dispose();
        }
    }

    private sealed class FolderSink(string root) : IPackageSink
    {
        public void Add(string entryPath, byte[] data)
        {
            // The entry paths are the game’s, which use forward slashes; on
            // the way to a folder they become this platform’s.
            var path = Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        public void Dispose() { }
    }
}
