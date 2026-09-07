using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
/// holding into the project's own files; <see cref="Write"/> puts those files in
/// a zip at the paths the game reads them from. Keeping them apart is what makes
/// a package reproducible: it ships what was saved, not a second rendering of
/// the same edits.
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
        TexconvRunner runner,
        List<string>? warnings = null)
    {
        ApplyToDatabase(project, document, kit, y2025, y2026);
        ApplyToLanguageFiles(project, y2025, y2026);
        ApplyLogos(project, logoEdits, runner, warnings ?? []);
    }

    /// <summary>
    /// Import the chosen pictures into the project's own branding archive.
    ///
    /// This is where a logo choice stops being a file path someone picked and
    /// becomes part of the project. It used to happen at package time, which
    /// meant a save threw the choice away — the tabs are rebuilt from the
    /// project's files afterwards, and the project had no record of it.
    ///
    /// Which kinds were touched is written into info.json, because the pixels
    /// alone don't say: a package merges only the resources that differ from
    /// the game's, and merging the rest would push one shader variant's bytes
    /// over the other's.
    /// </summary>
    private static void ApplyLogos(
        GridProject project, IReadOnlyList<LogoEdit> edits, TexconvRunner runner, List<string> warnings)
    {
        if (edits.Count == 0) return;

        var branding = project.BrandingErpPath;
        if (!File.Exists(branding))
        {
            warnings.Add("The project's team-branding archive is missing, so the logo changes couldn't be saved.");
            return;
        }

        // The whole archive, not an overlay: this one is being written back, so
        // everything it holds has to survive the round trip.
        var archive = ErpFileIO.Load(branding);

        var kinds = new SortedSet<string>(project.Info.LogoKinds, StringComparer.Ordinal);
        var imported = 0;

        foreach (var edit in edits)
        {
            try
            {
                // One at a time, and a failure is a note rather than the end of
                // the save. A picture at the wrong size is the common case —
                // every slice of an array shares one size and format — and it
                // must not take the names, colours and grid down with it.
                var (added, skipped) = LogoBaker.ImportAll(archive, [edit], runner);
                warnings.AddRange(skipped);
                if (added == 0) continue;

                imported += added;
                kinds.Add(edit.Kind.Id);
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(edit.PngPath)} couldn't be used: {ex.Message}");
            }
        }

        if (imported == 0) return;

        ErpFileIO.Save(archive, branding);
        project.Info.LogoKinds = kinds.ToList();
    }

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
    /// Zip the project's files at the paths the game reads them from.
    ///
    /// Nothing is rebuilt here: whatever the project holds is what ships, which
    /// is why the caller saves first. Logos are the exception, because the UI
    /// shader packages they merge into belong to the install rather than to the
    /// project.
    /// </summary>
    public static GridPackageResult Write(
        string zipPath,
        GridProject project,
        string? gameDir)
    {
        var warnings = new List<string>();

        // Build beside the destination and move it into place at the end, so a
        // half-written zip never appears at the path the user is watching.
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.zip.part");

        var languageFiles = 0;
        var database = false;
        var shaderPackages = 0;

        try
        {
            using (var stream = File.Create(temporary))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var path in project.LanguageFiles.OrderBy(f => f))
                {
                    Add(zip, string.Join('/', GameFiles.PackageLocalisationPath.Append(Path.GetFileName(path))),
                        File.ReadAllBytes(path));
                    languageFiles++;
                }
                if (languageFiles == 0)
                    warnings.Add("The project has no language files, so no names were packaged.");

                if (File.Exists(project.DatabasePath))
                {
                    Add(zip, string.Join('/', new[] { GameFiles.GameFolderName }.Concat(GameFiles.GameDatabaseRelPath)),
                        File.ReadAllBytes(project.DatabasePath));
                    database = true;
                }
                else
                {
                    warnings.Add("The project has no database, so no teams, seats or drivers were packaged.");
                }

                if (project.Info.LogoKinds.Count > 0)
                    shaderPackages = WriteShaderPackages(zip, project, gameDir, warnings);

                Add(zip, "mod.serps", Encoding.UTF8.GetBytes(BuildModSerps(project, gameDir)));
            }

            File.Move(temporary, zipPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }

        return new GridPackageResult(languageFiles, database, shaderPackages, warnings);
    }

    /// <summary>
    /// Bake the logo edits into both shader packages and add them.
    ///
    /// These come from the game rather than from the project, and are the one
    /// part of a package that cannot be built without an install. A failure here
    /// is reported and the rest of the package still ships — names, colours and
    /// the grid are worth having without logos.
    /// </summary>
    private static int WriteShaderPackages(
        ZipArchive zip, GridProject project, string? gameDir, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            warnings.Add("Logos need the game's UI shader packages, and F1 25 wasn't found — they were left out.");
            return 0;
        }

        var branding = project.BrandingErpPath;
        if (!File.Exists(branding))
        {
            warnings.Add("The project's team-branding archive is missing — logos were left out.");
            return 0;
        }

        var shaderDirectory = Path.Combine(gameDir, GameFiles.ShaderFolderName);
        var written = 0;

        try
        {
            // The pictures went into the project's archive when it was saved, so
            // there is nothing to import here — only the resources that now
            // differ from the game's, lifted out and merged over its own.
            var identifiers = project.Info.LogoKinds
                .Select(TeamLogos.Kind)
                .SelectMany(kind => kind.ResourceIdentifiers);
            var (overlay, _) = LogoBaker.BuildOverlay(branding, identifiers);

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
                    Add(zip, $"{GameFiles.GameFolderName}/{GameFiles.ShaderFolderName}/{name}", File.ReadAllBytes(baked));
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

    private static void Add(ZipArchive zip, string entryPath, byte[] data)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}
