using System.Text.RegularExpressions;
using SimplifiedDriverwearSwapper.Core.Toolkit;

namespace SimplifiedDriverwearSwapper.Core.Driverwear;

/// <summary>
/// Pulls the pieces a driverwear swap works from out of the game files, into a
/// working folder beside the app.
///
/// Every step is a Toolkit call — this class only decides which resources each
/// output holds. The filters below were derived by diffing a hand-made reference
/// set and reproduce it exactly, resource for resource.
/// </summary>
public static class DriverwearPrepare
{
    public sealed class PrepareOptions
    {
        public required string GameDir { get; init; }

        /// <summary>The set whose wear is being taken.</summary>
        public required DriverwearCatalogue.DriverwearEntry Driverwear { get; init; }

        /// <summary>The set that will wear it.</summary>
        public required DriverwearCatalogue.DriverwearEntry Team { get; init; }

        /// <summary>Where the extracts land. Emptied first, so each run starts clean.</summary>
        public required string WorkingDir { get; init; }

        /// <summary>The single ERP everything in Final is merged into.</summary>
        public required string OutputPath { get; init; }
    }

    public sealed class PrepareResult
    {
        public string WorkingDir { get; init; } = "";

        /// <summary>Files produced, relative to the working folder.</summary>
        public List<string> Outputs { get; } = new();

        public List<string> Warnings { get; } = new();

        public int TextureCount { get; set; }
        public int DriverCount { get; set; }

        /// <summary>The merged ERP, once written.</summary>
        public string? OutputPath { get; set; }

        /// <summary>Resources it ended up holding.</summary>
        public int OutputResources { get; set; }
    }

    /// <summary>Subfolder names, matching how the reference set is laid out.</summary>
    private const string DriverwearDir = "Driverwear";
    private const string TeamDir = "Team";

    /// <summary>Where the pieces that go into the finished ERP are gathered.</summary>
    private const string FinalDir = "Final";

    public static async Task<PrepareResult> RunAsync(
        PrepareOptions options, Action<string>? log = null, CancellationToken cancel = default)
    {
        var result = new PrepareResult { WorkingDir = options.WorkingDir };

        if (options.Driverwear.ErpPath is null)
            throw new InvalidOperationException($"{options.Driverwear.Name} has no ERP to take driverwear from.");
        if (options.Team.ErpPath is null)
            throw new InvalidOperationException($"{options.Team.Name} has no ERP to put driverwear on.");

        var wearDir = Path.Combine(options.WorkingDir, DriverwearDir);
        var teamDir = Path.Combine(options.WorkingDir, TeamDir);
        var tempDir = Path.Combine(options.WorkingDir, ".tmp");

        ResetDirectory(options.WorkingDir);
        Directory.CreateDirectory(wearDir);
        Directory.CreateDirectory(teamDir);
        Directory.CreateDirectory(tempDir);

        var wear = options.Driverwear;
        var team = options.Team;

        // ── Driverwear: the wear being taken ────────────────────────────────
        log?.Invoke($"Driverwear — {wear.Name}");

        await ExtractAsync(wear.ErpPath, Path.Combine(wearDir, "lods.erp"),
            @"\?lod=", result, $"{DriverwearDir}/lods.erp", log, cancel);

        await ExtractAsync(wear.ErpPath, Path.Combine(wearDir, "model+render.erp"),
            ModelRenderFilter(wear), result, $"{DriverwearDir}/model+render.erp", log, cancel);

        // Full resolution, which means resolving the external .mipmaps sidecars —
        // without them these come out at their low-res in-archive size.
        var texturesOut = Path.Combine(wearDir, "textures");
        var texResult = await Erp.ExportTexturesAsync(
            wear.ErpPath, texturesOut, withMips: true, gameDir: options.GameDir, cancel: cancel);

        if (texResult.Succeeded)
        {
            result.TextureCount = Directory.Exists(texturesOut)
                ? Directory.GetFiles(texturesOut, "*", SearchOption.AllDirectories).Length
                : 0;
            result.Outputs.Add($"{DriverwearDir}/textures/  ({result.TextureCount} files)");
            log?.Invoke($"    textures            {result.TextureCount} files at full resolution");
        }
        else
        {
            result.Warnings.Add($"Texture export failed: {texResult.ErrorMessage}");
            log?.Invoke($"    textures            FAILED — {texResult.ErrorMessage}");
        }

        // ── Team: the slot being dressed ────────────────────────────────────
        log?.Invoke("");
        log?.Invoke($"Team — {team.Name}");

        await ExtractAsync(team.ErpPath, Path.Combine(teamDir, "model+render.erp"),
            ModelRenderFilter(team), result, $"{TeamDir}/model+render.erp", log, cancel);

        // A MyTeam suit has no drivers of its own, so an empty list is expected
        // there; for a real team it means something is wrong with the install.
        var drivers = DriverLocator.FindDrivers(options.GameDir, team.Name);
        result.DriverCount = drivers.Count;
        if (drivers.Count == 0 && team.Kind == DriverwearCatalogue.WearSetKind.TeamLivery)
            result.Warnings.Add($"No driver folders found for {team.Name}.");

        // The team's textures: its own, plus each driver's boots and cap. Helmet
        // and visor are left out — they aren't driverwear.
        var texturesErp = Path.Combine(teamDir, "textures.erp");
        await ExtractAsync(team.ErpPath, texturesErp,
            Regex.Escape(team.TexturesRoot), result, $"{TeamDir}/textures.erp", log, cancel);

        var driverTextureParts = new List<string>();
        foreach (var driver in drivers)
        {
            cancel.ThrowIfCancellationRequested();
            if (driver.ErpPath is null)
            {
                result.Warnings.Add($"{driver.DriverName} has no ERP.");
                continue;
            }

            var part = Path.Combine(tempDir, $"tex_{driver.DriverName}.erp");
            var partResult = await Erp.ExtractToErpAsync(
                driver.ErpPath, part,
                $"textures/{Regex.Escape(driver.DriverName)}_(boots|cap)_", cancel);

            if (partResult.Succeeded) driverTextureParts.Add(part);
            else result.Warnings.Add($"No boots/cap textures for {driver.DriverName}: {partResult.ErrorMessage}");
        }

        if (driverTextureParts.Count > 0 && File.Exists(texturesErp))
        {
            var merge = await Erp.MergeAsync(texturesErp, driverTextureParts, cancel: cancel);
            if (!merge.Succeeded) result.Warnings.Add($"Merging driver textures failed: {merge.ErrorMessage}");
            else log?.Invoke($"    textures.erp        + {driverTextureParts.Count} driver texture set(s)");
        }

        // Embed the external mip chains so the archive carries its own images.
        if (File.Exists(texturesErp))
        {
            var strip = await Erp.StripMipsAsync(texturesErp, options.GameDir, cancel: cancel);
            if (!strip.Succeeded) result.Warnings.Add($"Stripping mipmaps failed: {strip.ErrorMessage}");
            else log?.Invoke("    textures.erp        mipmaps embedded");
        }

        // Each driver's own model/render resources.
        var driverErpNames = new List<string>();
        foreach (var driver in drivers)
        {
            cancel.ThrowIfCancellationRequested();
            if (driver.ErpPath is null) continue;

            var name = $"{Surname(driver.DriverName)}_model+renders.erp";
            await ExtractAsync(driver.ErpPath, Path.Combine(teamDir, name),
                @"\?(model|render)$", result, $"{TeamDir}/{name}", log, cancel);
            driverErpNames.Add(name);
        }

        // ── Assemble what goes into the finished ERP ────────────────────────
        // The geometry is the driverwear's throughout; only the identifiers move
        // over to the team and its drivers, so the game loads the donor's wear
        // under names it already expects. Textures stay the team's.
        log?.Invoke("");
        log?.Invoke("Final");

        var finalDir = Path.Combine(options.WorkingDir, FinalDir);
        Directory.CreateDirectory(finalDir);

        Collect(Path.Combine(wearDir, "lods.erp"), finalDir, result, log);
        Collect(Path.Combine(teamDir, "textures.erp"), finalDir, result, log);

        await ImportWearTexturesAsync(
            Path.Combine(wearDir, "textures"), Path.Combine(finalDir, "textures.erp"),
            Path.Combine(options.WorkingDir, ".tex"), wear, team, drivers, result, log, cancel);

        var wearModelRender = Path.Combine(wearDir, "model+render.erp");

        // The team's own copy: same resources under the team's identifiers. The
        // root is swapped first and then the file-name token, because the two
        // kinds don't share a root — a MyTeam suit's models live under
        // livery_package/ where a team's live under character_package/.
        await RebadgeAsync(wearModelRender, Path.Combine(finalDir, "model+render.erp"),
            renames: new[] { (wear.IdfRoot, team.IdfRoot), (wear.Name + "_", team.Name + "_") },
            result, "model+render.erp", log, cancel);

        // One per driver, from the same source: keep the half matching their
        // gender, then rename it to them.
        foreach (var driver in drivers)
        {
            cancel.ThrowIfCancellationRequested();
            await BuildDriverModelRenderAsync(wearModelRender, finalDir, wear, team, driver, result, log, cancel);
        }

        await MergeFinalAsync(finalDir, options.OutputPath, result, log, cancel);

        TryDelete(tempDir);
        return result;
    }

    /// <summary>
    /// Merge everything in Final into the one ERP the swap produces.
    ///
    /// The first file becomes the base and the rest are merged into it, written
    /// out to the destination so nothing in Final is modified. The pieces hold
    /// disjoint resources by construction, so merge order doesn't decide any
    /// outcome — but a conflict would be a sign the pieces overlap, so it's
    /// surfaced rather than silently resolved.
    /// </summary>
    private static async Task MergeFinalAsync(
        string finalDir, string outputPath, PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        log?.Invoke("");
        log?.Invoke("Output");

        var pieces = Directory.Exists(finalDir)
            ? Directory.GetFiles(finalDir, "*.erp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        if (pieces.Count == 0)
        {
            result.Warnings.Add("Nothing in Final to merge.");
            log?.Invoke("    (nothing to merge)");
            return;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

        var merge = await Erp.MergeAsync(pieces[0], pieces.Skip(1), outputPath, cancel: cancel);
        if (!merge.Succeeded)
        {
            result.Warnings.Add($"Merging into the output failed: {merge.ErrorMessage}");
            log?.Invoke($"    merge               FAILED — {merge.ErrorMessage}");
            return;
        }

        foreach (var piece in pieces)
            log?.Invoke($"    + {Path.GetFileName(piece)}");

        // A skip means two pieces claimed the same identifier and one was dropped.
        if (merge.Skipped > 0)
            result.Warnings.Add($"{merge.Skipped} resource(s) appeared in more than one Final piece and were kept from the first.");

        result.OutputPath = outputPath;
        result.OutputResources = merge.Total;

        log?.Invoke($"    {Path.GetFileName(outputPath)} — {result.OutputResources} resources");
    }

    /// <summary>
    /// Put the driverwear's images into the team's textures.
    ///
    /// The counterpart to the model+render rebadge, the other way round: there the
    /// identifiers moved to the team, here the identifiers stay and the images
    /// underneath them change. Either way the team keeps its names and the
    /// driverwear supplies what they point at.
    ///
    /// <c>textures import</c> matches a file to a resource by that resource's own
    /// folder and name, so each image is staged at the path the <i>target</i>
    /// expects. Images are paired by what follows the set's token —
    /// <c>&lt;wear&gt;_livery_v2_d</c> supplies <c>&lt;team&gt;_livery_v2_d</c> — which is
    /// what lets a MyTeam suit and a team livery swap either way despite living
    /// under entirely different roots.
    /// </summary>
    private static async Task ImportWearTexturesAsync(
        string wearTexturesDir, string targetErp, string stagingDir,
        DriverwearCatalogue.DriverwearEntry wear, DriverwearCatalogue.DriverwearEntry team,
        List<DriverLocator.DriverFolder> drivers,
        PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        // The wear's own textures inside its export, and where the team keeps its.
        var source = Path.Combine(wearTexturesDir, wear.TexturesRoot.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(stagingDir, team.TexturesRoot.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(source) || !File.Exists(targetErp))
        {
            result.Warnings.Add("Skipped importing the driverwear's textures — an earlier step didn't produce its input.");
            log?.Invoke("    textures.erp        SKIPPED — nothing to import from");
            return;
        }

        TryDelete(stagingDir);
        Directory.CreateDirectory(target);

        // Everything the wear has, renamed onto the team's token.
        var staged = 0;
        var prefix = wear.Name + "_";
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var suffix = name[prefix.Length..];
            try
            {
                File.Copy(file, Path.Combine(target, team.Name + "_" + suffix), overwrite: true);
                staged++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Couldn't stage {name}: {ex.Message}");
            }
        }

        // Each driver wears the donor's boots and cap rather than their own.
        var perDriver = 0;
        foreach (var driver in drivers)
        {
            cancel.ThrowIfCancellationRequested();
            perDriver += StageDriverTextures(source, stagingDir, wear, team, driver, result);
        }

        var import = await Erp.ImportTexturesAsync(targetErp, stagingDir, cancel: cancel);

        if (!import.Succeeded)
        {
            result.Warnings.Add($"Importing the driverwear's textures failed: {import.ErrorMessage}");
            log?.Invoke($"    textures.erp        FAILED — {import.ErrorMessage}");
            return;
        }

        if (staged == 0)
            result.Warnings.Add("The driverwear had no textures to stage, so the team kept its own.");

        log?.Invoke($"    textures.erp        driverwear images imported" +
                    $" ({import.Imported})" +
                    (perDriver > 0 ? $", {perDriver} for drivers" : ""));

        TryDelete(stagingDir);
    }

    /// <summary>
    /// Stage the driverwear's boots and cap under one driver's name, so the
    /// driver wears the donor's kit rather than their own.
    /// </summary>
    private static int StageDriverTextures(
        string wearTextures, string stagingDir,
        DriverwearCatalogue.DriverwearEntry wear, DriverwearCatalogue.DriverwearEntry team,
        DriverLocator.DriverFolder driver, PrepareResult result)
    {
        // Drivers always live under character_package, whatever root their team has.
        var destination = Path.Combine(stagingDir, "character_package", "drivers",
            driver.Gender, $"{team.Name}_{driver.DriverName}", "textures");
        Directory.CreateDirectory(destination);

        var copied = 0;
        foreach (var part in new[] { "boots", "cap" })
        {
            foreach (var map in new[] { "d", "n", "s" })
            {
                var file = $"{wear.Name}_{part}_{map}.tif.dds";
                var from = Path.Combine(wearTextures, file);
                if (!File.Exists(from)) continue;

                try
                {
                    File.Copy(from, Path.Combine(destination, $"{driver.DriverName}_{part}_{map}.tif.dds"), true);
                    copied++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Couldn't stage {file} for {driver.DriverName}: {ex.Message}");
                }
            }
        }

        if (copied == 0)
            result.Warnings.Add($"No boots or cap textures found in the driverwear for {driver.DriverName}.");
        return copied;
    }

    /// <summary>
    /// Copy an ERP and apply a series of identifier find/replaces to the copy.
    /// </summary>
    private static async Task<bool> RebadgeAsync(
        string source, string destination, IEnumerable<(string Find, string Replace)> renames,
        PrepareResult result, string label, Action<string>? log, CancellationToken cancel)
    {
        if (!File.Exists(source))
        {
            result.Warnings.Add($"{label}: {Path.GetFileName(source)} wasn't produced.");
            log?.Invoke($"    {label,-19} MISSING");
            return false;
        }

        try { File.Copy(source, destination, overwrite: true); }
        catch (Exception ex)
        {
            result.Warnings.Add($"{label}: {ex.Message}");
            log?.Invoke($"    {label,-19} FAILED — {ex.Message}");
            return false;
        }

        foreach (var (find, replace) in renames)
        {
            if (find == replace) continue;
            var run = await Erp.RenameAllAsync(destination, find, replace, cancel);
            if (run.Succeeded) continue;

            result.Warnings.Add($"{label}: renaming '{find}' failed: {run.ErrorMessage}");
            log?.Invoke($"    {label,-19} FAILED — {run.ErrorMessage}");
            return false;
        }

        result.Outputs.Add($"{FinalDir}/{label}");
        log?.Invoke($"    {label,-19} built");
        return true;
    }

    /// <summary>
    /// Build a driver's model/render ERP from the driverwear's.
    ///
    /// The driverwear carries a male and a female variant of each part; a driver
    /// needs only their own, under their own name. So the gender-matching half is
    /// taken and the identifiers are rewritten:
    /// <c>team_livery/&lt;wear&gt;/idf/&lt;wear&gt;_body_logos_male.idf</c> becomes
    /// <c>drivers/male/&lt;team&gt;_&lt;driver&gt;/idf/&lt;driver&gt;_body_logos.idf</c>,
    /// which is what the driver's own ERP calls it.
    /// </summary>
    private static async Task BuildDriverModelRenderAsync(
        string wearModelRender, string finalDir,
        DriverwearCatalogue.DriverwearEntry wear, DriverwearCatalogue.DriverwearEntry team,
        DriverLocator.DriverFolder driver,
        PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        var label = $"{Surname(driver.DriverName)}_model+renders.erp";
        var destination = Path.Combine(finalDir, label);

        if (!File.Exists(wearModelRender))
        {
            result.Warnings.Add($"{label}: the driverwear's model+render wasn't produced.");
            log?.Invoke($"    {label,-19} MISSING");
            return;
        }

        // "_male" as its own suffix, so it can't also match "_female".
        var genderSuffix = "_" + driver.Gender;
        var extract = await Erp.ExtractToErpAsync(
            wearModelRender, destination, $@"{Regex.Escape(genderSuffix)}\.idf\?", cancel);

        if (!extract.Succeeded)
        {
            result.Warnings.Add($"{label}: {extract.ErrorMessage}");
            log?.Invoke($"    {label,-19} FAILED — {extract.ErrorMessage}");
            return;
        }

        // Longest first: the bare "<wear>_male.idf" rename must not be applied
        // before the parts that also end in "_male.idf".
        var renames = new[]
        {
            (wear.IdfRoot, $"character_package/drivers/{driver.Gender}/{team.Name}_{driver.DriverName}/idf/"),
            ($"{wear.Name}_body_logos{genderSuffix}.idf", $"{driver.DriverName}_body_logos.idf"),
            ($"{wear.Name}_glove_logos{genderSuffix}.idf", $"{driver.DriverName}_glove_logos.idf"),
            ($"{wear.Name}{genderSuffix}.idf", $"{driver.DriverName}.idf"),
        };

        foreach (var (find, replace) in renames)
        {
            var run = await Erp.RenameAllAsync(destination, find, replace, cancel);
            if (run.Succeeded) continue;

            result.Warnings.Add($"{label}: renaming '{find}' failed: {run.ErrorMessage}");
            log?.Invoke($"    {label,-19} FAILED — {run.ErrorMessage}");
            return;
        }

        result.Outputs.Add($"{FinalDir}/{label}");
        log?.Invoke($"    {label,-19} built for {driver.DisplayName}");
    }

    /// <summary>
    /// Copy one prepared ERP into the final folder. A missing source is recorded
    /// rather than thrown, so one failed extract doesn't lose the rest.
    /// </summary>
    private static void Collect(string source, string finalDir, PrepareResult result, Action<string>? log)
    {
        var name = Path.GetFileName(source);
        if (!File.Exists(source))
        {
            result.Warnings.Add($"{name} wasn't produced, so it isn't in {FinalDir}.");
            log?.Invoke($"    {name,-19} MISSING");
            return;
        }

        try
        {
            File.Copy(source, Path.Combine(finalDir, name), overwrite: true);
            result.Outputs.Add($"{FinalDir}/{name}");
            log?.Invoke($"    {name,-19} copied");
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Couldn't copy {name} into {FinalDir}: {ex.Message}");
            log?.Invoke($"    {name,-19} FAILED — {ex.Message}");
        }
    }

    /// <summary>
    /// Only the set's own <c>idf/</c> entries. The shared
    /// <c>condition_scene/idf/</c> ones are also <c>?model</c> and <c>?render</c>
    /// files, and the reference leaves them out.
    ///
    /// Built from the set's own root, since a MyTeam suit lives under
    /// <c>livery_package/</c> rather than <c>character_package/team_livery/</c>.
    /// </summary>
    private static string ModelRenderFilter(DriverwearCatalogue.DriverwearEntry set) =>
        $@"{Regex.Escape(set.IdfRoot)}.*\?(model|render)$";

    /// <summary>
    /// The file name a driver's extract takes — <c>isack_hadjar</c> becomes
    /// <c>hadjar</c>, matching the reference.
    /// </summary>
    private static string Surname(string driverName)
    {
        var underscore = driverName.LastIndexOf('_');
        return underscore >= 0 && underscore < driverName.Length - 1
            ? driverName[(underscore + 1)..]
            : driverName;
    }

    private static async Task ExtractAsync(
        string sourceErp, string outputErp, string filter,
        PrepareResult result, string label, Action<string>? log, CancellationToken cancel)
    {
        var run = await Erp.ExtractToErpAsync(sourceErp, outputErp, filter, cancel);

        if (!run.Succeeded)
        {
            result.Warnings.Add($"{label}: {run.ErrorMessage}");
            log?.Invoke($"    {Path.GetFileName(outputErp),-19} FAILED — {run.ErrorMessage}");
            return;
        }

        result.Outputs.Add(label);
        log?.Invoke($"    {Path.GetFileName(outputErp),-19} {run.Taken} resources");
    }

    /// <summary>
    /// Empty the working folder so a run never mixes with the last one's output.
    /// Recreated rather than deleted outright, so a folder open in Explorer
    /// doesn't make the run fail.
    /// </summary>
    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var dir in Directory.EnumerateDirectories(path)) TryDelete(dir);
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try { File.Delete(file); } catch { /* left behind; the run overwrites what it needs */ }
            }
        }
        Directory.CreateDirectory(path);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { /* best effort */ }
    }
}
