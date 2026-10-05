using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EgoEngineLibrary.Data.GameDatabases;
using SimplifiedTeamEditor.Core.Lng;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Teams;
using EgoEngineLibrary.Texconv;

namespace SimplifiedTeamEditor.Core.Packaging;

/// <summary>What the package says about itself, shown wherever mods are listed.</summary>
public sealed record ModInfo(string Name, string? Author, string? Version);

/// <summary>One season's changes: the names as they now read, and the colours that differ.</summary>
public sealed record SeasonEdits(
    Season Season,
    IReadOnlyList<TeamNames> Names,
    IReadOnlyList<TeamNames> ChangedNames,
    IReadOnlyDictionary<string, Dictionary<ColourField, string>> ChangedColours)
{
    public bool AnyNames => ChangedNames.Count > 0;
    public bool AnyColours => ChangedColours.Count > 0;
    public bool Any => AnyNames || AnyColours;
}

/// <summary>What a package turned out to contain.</summary>
public sealed record PackageResult(
    int LanguageFiles,
    bool Database,
    int Colours2025,
    int Reassigned2026,
    int Colours2026,
    int Keys2026Added,
    int ShaderPackages,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Writing team edits out as a mod anyone can install.
///
/// Built from the game's own files, not from copies carried here: the language
/// files come out of the exe, the database out of the install when it is one we
/// recognise, and the UI shader packages a logo edit merges into have to come
/// from there in any case. What that costs is a game folder - none of this
/// works without one - and what it buys is a package that is current with
/// whatever the game has patched to rather than with whatever we last bundled.
///
/// The clean-room half of the old arrangement is kept by checking rather than
/// by copying: see GameFileGuard and GameDatabaseSource, which is what stops
/// somebody else's mod becoming the base of this one.
/// </summary>
public static class TeamPackager
{
    public static PackageResult Write(
        string zipPath,
        string assetsDir,
        SeasonEdits y2025,
        SeasonEdits y2026,
        ModInfo? modInfo,
        IReadOnlyList<LogoEdit> logoEdits,
        string? gameDir,
        string languageDir,
        TexconvRunner runner,
        IProgress<string>? say = null)
    {
        var warnings = new List<string>();

        // The language files came out of the game rather than out of a copy we
        // carry — taken by the app when it opened, into the folder named here.
        // See Simplified.Language.LanguageFiles.
        if (!Directory.Exists(languageDir) ||
            !Directory.EnumerateFiles(languageDir, "language_*.lng").Any())
            throw new InvalidOperationException(
                "The game's language files aren't here, and a package is built from them. They come out " +
                "of the game's own exe, so set the game folder in Settings — the app takes them the next " +
                "time it opens.");

        // The database and the 2026 key additions are worked out together: a
        // 2026 name edit changes both, and the key map has to be read off the
        // database before reassignment rewrites the fields it reads.
        say?.Report("Adding your team colours…");
        var (databaseBytes, keys2026, counts) = BuildDatabaseAndKeys(assetsDir, gameDir, y2025, y2026, warnings);

        // Build beside the destination and move it into place at the end, so a
        // half-written zip never appears at the path the user is watching.
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.zip.part");

        var languageFiles = 0;
        var shaderPackages = 0;

        try
        {
            using (var stream = File.Create(temporary))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                say?.Report("Adding your team names…");
                languageFiles = WriteLanguageFiles(zip, languageDir, y2025, keys2026, warnings);

                if (databaseBytes is not null)
                    Write(zip, string.Join('/', new[] { GameFiles.GameFolderName }
                                                .Concat(GameFiles.GameDatabaseRelPath)), databaseBytes);

                if (logoEdits.Count > 0)
                {
                    // The long one: every logo is converted, put into the
                    // branding archive and merged into both of the game's
                    // shader packages, which are 300 MB each.
                    say?.Report("Adding your logos…");
                    shaderPackages = WriteShaderPackages(zip, assetsDir, logoEdits, gameDir, runner, warnings);
                }

                if (modInfo is not null && !string.IsNullOrWhiteSpace(modInfo.Name))
                    Write(zip, "mod.serps", Encoding.UTF8.GetBytes(BuildModSerps(modInfo, gameDir)));
            }

            say?.Report("Finishing your mod…");
            File.Move(temporary, zipPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }

        return new PackageResult(
            languageFiles, databaseBytes is not null,
            counts.Colours2025, counts.Reassigned, counts.Colours2026,
            keys2026.Count, shaderPackages, warnings);
    }

    // ───────────────────────── The database ───────────────────────────

    private static (byte[]? Bytes, Dictionary<string, string> Keys2026,
                    (int Colours2025, int Reassigned, int Colours2026) Counts)
        BuildDatabaseAndKeys(string assetsDir, string? gameDir, SeasonEdits y2025, SeasonEdits y2026,
                             List<string> warnings)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!y2025.Any && !y2026.Any) return (null, empty, (0, 0, 0));

        // The installed game's own database when it is one we recognise, and the
        // bundled copy otherwise. Taking the game's keeps a package current with
        // whatever it has patched to; recognising it first is what stops somebody
        // else's database mod becoming the base of this one.
        var choice = Simplified.Shared.GameDatabaseSource.Resolve(
            gameDir, Path.Combine(assetsDir, "gamedatabases.2025.bin"));

        if (choice.Note is not null) warnings.Add(choice.Note);

        var path = choice.Path;
        if (!File.Exists(path))
        {
            warnings.Add("No team database could be found, so no colours or 2026 names can be packaged.");
            return (null, empty, (0, 0, 0));
        }

        var db = GameDatabase.Load(path);

        // Seed every 2026 key with its default and let edits override, so a
        // repointed field can never end up aimed at a key that isn't there.
        var keys2026 = empty;
        if (y2026.AnyNames)
        {
            keys2026 = Season2026.LoadDefaults(assetsDir);
            foreach (var team in y2026.ChangedNames)
            {
                if (!string.IsNullOrEmpty(team.Full)) keys2026[team.Team.FullKey] = team.Full;
                if (!string.IsNullOrEmpty(team.Short)) keys2026[team.Team.ShortKey] = team.Short;
                if (!string.IsNullOrEmpty(team.SuperShort)) keys2026[team.Team.SuperShortKey] = team.SuperShort;
            }
        }

        // Layered onto the one copy, in this order: 2025 colours, then the
        // 2026 repointing, then 2026 colours.
        var colours2025 = ApplyColours(db, Season.Y2025, y2025.ChangedColours);

        var reassigned = 0;
        if (y2026.AnyNames)
            reassigned = Season2026.ApplyReassignment(db, Season2026.LoadReassignment(assetsDir));

        var colours2026 = ApplyColours(db, Season.Y2026, y2026.ChangedColours);

        var changed = colours2025 + reassigned + colours2026;
        return (changed > 0 ? db.ToBytes() : null, keys2026, (colours2025, reassigned, colours2026));
    }

    private static int ApplyColours(
        GameDatabase db, Season season,
        IReadOnlyDictionary<string, Dictionary<ColourField, string>> changed)
    {
        var applied = 0;
        foreach (var team in TeamGrid.For(season))
        {
            if (!changed.TryGetValue(team.Id, out var fields)) continue;

            var index = TeamColours.FindIndex(db, team.DatabaseId);
            if (index is null) continue;

            foreach (var (field, hex) in fields)
                if (TeamColours.Write(db, index.Value, field, hex)) applied++;
        }
        return applied;
    }

    // ───────────────────────── Language files ─────────────────────────

    /// <summary>
    /// Every language gets the same edits. A team renamed in English is renamed
    /// in all of them — the alternative is a game that calls the team one thing
    /// in menus and another in commentary subtitles.
    /// </summary>
    private static int WriteLanguageFiles(
        ZipArchive zip, string languageDir, SeasonEdits y2025,
        IReadOnlyDictionary<string, string> keys2026, List<string> warnings)
    {
        var directory = languageDir;
        if (!Directory.Exists(directory))
        {
            warnings.Add("The bundled language files are missing, so no names can be packaged.");
            return 0;
        }

        var edits = TeamNameEditing.BuildEdits(y2025.ChangedNames);
        if (edits.Count == 0 && keys2026.Count == 0) return 0;

        var written = 0;
        foreach (var path in Directory.GetFiles(directory, "*.lng").OrderBy(f => f))
        {
            try
            {
                var lng = LngFile.Load(path);

                byte[] data;
                if (keys2026.Count > 0)
                {
                    // Adding keys changes which bucket everything hashes into,
                    // so the whole file is rebuilt rather than patched.
                    var all = new Dictionary<string, string>(lng.Entries, StringComparer.Ordinal);
                    foreach (var (key, value) in edits) all[key] = value;
                    foreach (var (key, value) in keys2026) all[key] = value;
                    data = lng.BuildRebuiltData(all);
                }
                else
                {
                    data = lng.BuildSaveData(edits);
                }

                Write(zip, string.Join('/', GameFiles.PackageLocalisationPath.Append(Path.GetFileName(path))), data);
                written++;
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(path)} could not be written: {ex.Message}");
            }
        }
        return written;
    }

    // ───────────────────────────── Logos ──────────────────────────────

    /// <summary>
    /// Bake the logo edits into both shader packages and add them.
    ///
    /// These come from the game rather than from the bundle, and are the one
    /// part of a package that cannot be built without an install. A failure
    /// here is reported and the rest of the package still ships — names and
    /// colours are worth having without logos.
    /// </summary>
    private static int WriteShaderPackages(
        ZipArchive zip, string assetsDir, IReadOnlyList<LogoEdit> edits,
        string? gameDir, TexconvRunner runner, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            warnings.Add("Logos need the game's UI shader packages, and F1 25 wasn't found — they were left out.");
            return 0;
        }

        var branding = Path.Combine(assetsDir, "ui", "ui_texarrays_team_branding.erp");
        if (!File.Exists(branding))
        {
            warnings.Add("The bundled team-branding archive is missing — logos were left out.");
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
            var (overlay, _) = LogoBaker.BuildOverlay(branding, edits);

            // A picture of another size or shape is fitted to the slot rather
            // than refused, and is said so; what could not be used at all costs
            // only itself.
            var (_, skipped, fitted) = LogoBaker.ImportAll(overlay, edits, runner);
            warnings.AddRange(SliceFit.Summarise(fitted));
            warnings.AddRange(skipped);

            // The pair the game expects in any mod that ships a shader package.
            // This app writes them the same way the Grid Editor does, and wants
            // them for the same reason.
            Simplified.Shared.SerpsOverlay.AddTo(overlay);

            var work = Path.Combine(Path.GetTempPath(), "simplified-team-editor", Guid.NewGuid().ToString("N"));
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
                    Write(zip, $"{GameFiles.GameFolderName}/{GameFiles.ShaderFolderName}/{name}", File.ReadAllBytes(baked));
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

    // ──────────────────────────── mod.serps ───────────────────────────

    /// <summary>
    /// The manifest, at the root where the launcher looks for it. The game it
    /// was built against is stamped from the install when there is one, and left
    /// blank otherwise, matching what other tools write.
    /// </summary>
    private static string BuildModSerps(ModInfo info, string? gameDir)
    {
        var manifest = new Dictionary<string, string> { ["mod_name"] = info.Name.Trim() };
        if (!string.IsNullOrWhiteSpace(info.Author)) manifest["author"] = info.Author.Trim();
        if (!string.IsNullOrWhiteSpace(info.Version)) manifest["version"] = info.Version.Trim();

        // Read the same way Studio and the Grid Editor read it. This used to
        // take the exe's ProductVersion, which on F1 25 is comma-separated —
        // so a build this app named 1,0,141,2878 was the one Studio called
        // 1.0.141.2878, and the two manifests would not compare equal.
        manifest["game"] = Simplified.Shared.GameVersion.ReadName(gameDir) ?? "";
        manifest["game_version"] = Simplified.Shared.GameVersion.Read(gameDir) ?? "";

        return JsonSerializer.Serialize(manifest, SerpsJson);
    }

    /// <summary>
    /// How the manifest is written — the options Studio uses, so all three apps
    /// spell a mod.serps identically. The encoder is the reason: the default one
    /// escapes the game's own name to <c>F1® 25</c>, where this writes
    /// <c>F1® 25</c>.
    /// </summary>
    private static readonly JsonSerializerOptions SerpsJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private static void Write(ZipArchive zip, string entryPath, byte[] data)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }
}
