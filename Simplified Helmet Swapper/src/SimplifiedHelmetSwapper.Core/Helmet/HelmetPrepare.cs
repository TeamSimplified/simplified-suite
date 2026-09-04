using System.Text.RegularExpressions;
using SimplifiedHelmetSwapper.Core.Toolkit;

namespace SimplifiedHelmetSwapper.Core.Helmet;

/// <summary>
/// Gives one driver another's helmet, by editing their own.
///
/// The output is the target's <c>&lt;driver&gt;_helmet.erp</c> with pieces
/// substituted, not a fresh archive holding only what changed — so the file
/// that comes out is a complete replacement for the one it is named after, and
/// everything the swap doesn't touch is still exactly where the game left it.
///
/// The shell and the rest are handled differently on purpose:
///
/// <list type="bullet">
/// <item>The generic shell — the Bell, Arai, Schuberth or Stilo the helmet is
/// built on — is swapped whole. The target's model, render and LODs are deleted
/// and the donor manufacturer's put in under <i>their own</i> names, because
/// those resources reference each other by name inside their payloads and
/// re-addressing them would leave every reference pointing at something that is
/// no longer there.</item>
/// <item>Everything the driver owns — attachments, materials, images — is
/// re-addressed to the target instead, the way the driverwear swap does it.
/// Those are per-driver resources, so they have to arrive under the target's
/// names to be found at all.</item>
/// </list>
///
/// No bytes inside a payload are ever patched, which is what lets two drivers
/// with names of different lengths swap at all.
/// </summary>
public static class HelmetPrepare
{
    public sealed class PrepareOptions
    {
        public required string GameDir { get; init; }

        /// <summary>The helmet being taken.</summary>
        public required HelmetCatalogue.Entry Donor { get; init; }

        /// <summary>The driver who will wear it, and whose file is being edited.</summary>
        public required HelmetCatalogue.Entry Target { get; init; }

        /// <summary>Where the extracts land. Emptied first, so each run starts clean.</summary>
        public required string WorkingDir { get; init; }

        /// <summary>The edited copy of the target's helmet.</summary>
        public required string OutputPath { get; init; }
    }

    public sealed class PrepareResult
    {
        public string WorkingDir { get; init; } = "";
        public List<string> Outputs { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Families actually carried over, and ones the donor didn't have.</summary>
        public List<string> Families { get; } = new();

        public string DonorShell { get; set; } = "";
        public string TargetShell { get; set; } = "";
        public bool ShellChanged => !DonorShell.Equals(TargetShell, StringComparison.OrdinalIgnoreCase);

        /// <summary>How much of the target's own helmet was left alone.</summary>
        public int Kept { get; set; }
        public int Replaced { get; set; }
        public int Added { get; set; }

        public string? OutputPath { get; set; }
        public int OutputResources { get; set; }
    }

    private const string PartsDir = "Parts";

    /// <summary>Model, render and the LOD meshes and buffers — the geometry, and nothing else.</summary>
    private const string GeometrySuffixes = @"\?(model|render|lod=)";

    public static async Task<PrepareResult> RunAsync(
        PrepareOptions options, Action<string>? log = null, CancellationToken cancel = default)
    {
        var d = options.Donor;
        var t = options.Target;
        var result = new PrepareResult { WorkingDir = options.WorkingDir };

        var donor = HelmetCatalogue.Read(d)
            ?? throw new InvalidOperationException($"{d.Label} has no helmet this app can read.");
        var target = HelmetCatalogue.Read(t)
            ?? throw new InvalidOperationException($"{t.Label} has no helmet this app can read.");

        result.DonorShell = donor.Shell;
        result.TargetShell = target.Shell;

        var parts = Path.Combine(options.WorkingDir, PartsDir);
        ResetDirectory(options.WorkingDir);
        Directory.CreateDirectory(parts);

        log?.Invoke($"{d.Label}  →  {t.Label}");
        log?.Invoke("");

        // ── Start from the target's own helmet ──────────────────────────────
        var outputDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        File.Copy(t.ErpPath, options.OutputPath, overwrite: true);

        result.Kept = target.ResourceCount;
        log?.Invoke($"    {Path.GetFileName(t.ErpPath),-24} {target.ResourceCount} resources, copied to edit");

        var dRoot = $"drivers/{d.Gender}/{d.Folder}/";
        var tRoot = $"drivers/{t.Gender}/{t.Folder}/";

        // ── The shell, swapped whole and kept under its own names ───────────
        if (result.ShellChanged)
        {
            var removed = await Erp.RemoveAllAsync(options.OutputPath,
                $@"helmets/{Regex.Escape(target.Shell)}/.*{GeometrySuffixes}", cancel);

            if (!removed.Succeeded)
            {
                result.Warnings.Add($"shell: couldn't remove {target.Shell}: {removed.ErrorMessage}");
                log?.Invoke($"    {"shell",-24} FAILED — {removed.ErrorMessage}");
            }
            else
            {
                log?.Invoke($"    {"shell",-24} {target.Shell} geometry removed");
            }
        }

        // With the same shell on both sides this replaces like for like, which
        // is a no-op in effect and keeps the one code path.
        await StageAsync(d.ErpPath, Path.Combine(parts, "1_shell.erp"),
            $@"helmets/{Regex.Escape(donor.Shell)}/.*{GeometrySuffixes}",
            [], $"shell {donor.Shell}", result, log, cancel);

        // ── The attachments the driver wears on it ──────────────────────────
        var attach = $"{Regex.Escape(d.Driver)}_helmet_attachments";

        await StageAsync(d.ErpPath, Path.Combine(parts, "2_attachments.erp"),
            $@"{Regex.Escape(dRoot)}idf/{attach}\.idf\?(model|render)$",
            [(dRoot, tRoot), ($"{d.Driver}_helmet_attachments", $"{t.Driver}_helmet_attachments"),
             ($"?context={d.Folder}", $"?context={t.Folder}")],
            "attachments", result, log, cancel);

        // Their geometry keeps the donor's names: the model above was
        // re-addressed but its payload was not, so what it points at has to
        // still be called what it was called.
        await StageAsync(d.ErpPath, Path.Combine(parts, "3_attachment_lods.erp"),
            $@"{Regex.Escape(dRoot)}idf/{attach}\.idf\?lod=",
            [], "attachment lods", result, log, cancel);

        // ── The four materials, and the images they name ────────────────────
        var families = HelmetCatalogue.HelmetFamilies.Where(f => donor.Families.Contains(f)).ToList();
        result.Families.AddRange(families);

        if (families.Count == 0)
            result.Warnings.Add($"{d.Label} has no helmet textures of their own, so there is nothing to take.");

        var group = string.Join('|', families.Select(Regex.Escape));

        if (families.Count > 0)
        {
            // Only the folder moves. A material is called the same thing for
            // everyone, and its payload still names the donor's images — which
            // is why they go in beside it, unrenamed.
            await StageAsync(d.ErpPath, Path.Combine(parts, "4_materials.erp"),
                $@"{Regex.Escape(dRoot)}materials/({group})\.material$",
                [(dRoot, tRoot)], "materials", result, log, cancel);

            await StageAsync(d.ErpPath, Path.Combine(parts, "5_textures.erp"),
                $@"{Regex.Escape(dRoot)}textures/{Regex.Escape(d.Driver)}_({group})_[dns]\.tif",
                [], "textures", result, log, cancel);
        }

        // A family the target has and the donor doesn't leaves the target's own
        // showing through, which is not the helmet that was asked for.
        foreach (var f in target.Families.Except(families))
            result.Warnings.Add(
                $"{d.Label} has no {f.Replace('_', ' ')} of their own, so {Pretty(t.Driver)} keeps theirs.");

        await ApplyAsync(parts, options.OutputPath, options.GameDir,
            $@"{Regex.Escape(dRoot)}textures/", result, log, cancel);
        return result;
    }

    /// <summary>
    /// Lift one piece out of the donor and re-address it, ready to go in.
    ///
    /// An extract that matches nothing is a warning rather than a failure: the
    /// helmets are not uniform — most drivers have four texture families, some
    /// have two — and one absent piece shouldn't lose the rest of the helmet.
    /// </summary>
    private static async Task StageAsync(
        string sourceErp, string destination, string filter,
        (string Find, string Replace)[] renames,
        string label, PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        var extract = await Erp.ExtractToErpAsync(sourceErp, destination, filter, cancel);
        if (!extract.Succeeded || !File.Exists(destination))
        {
            result.Warnings.Add($"{label}: {extract.ErrorMessage}");
            log?.Invoke($"    {label,-24} nothing matched");
            return;
        }

        foreach (var (find, replace) in renames)
        {
            if (find == replace) continue;
            var run = await Erp.RenameAllAsync(destination, find, replace, cancel);
            if (run.Succeeded) continue;

            result.Warnings.Add($"{label}: renaming '{find}' failed: {run.ErrorMessage}");
            log?.Invoke($"    {label,-24} FAILED — {run.ErrorMessage}");
            return;
        }

        result.Outputs.Add($"{PartsDir}/{Path.GetFileName(destination)}");
        log?.Invoke($"    {label,-24} {extract.Taken} resources" +
                    (renames.Length > 0 ? ", re-addressed" : ", under their own names"));
    }

    /// <summary>
    /// Put the pieces into the copy, overwriting where the target already has
    /// that identifier, and pull in the external mip chains so the file carries
    /// its own images rather than depending on sidecars belonging to the donor.
    /// </summary>
    private static async Task ApplyAsync(
        string partsDir, string outputPath, string gameDir, string mipFilter,
        PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        log?.Invoke("");

        var pieces = Directory.Exists(partsDir)
            ? Directory.GetFiles(partsDir, "*.erp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (pieces.Count == 0)
        {
            result.Warnings.Add("Nothing was taken from the donor, so the helmet is unchanged.");
            log?.Invoke("    (nothing to put in)");
            return;
        }

        var merge = await Erp.MergeAsync(outputPath, pieces, outputPath, overwrite: true, cancel);
        if (!merge.Succeeded)
        {
            result.Warnings.Add($"Putting the pieces in failed: {merge.ErrorMessage}");
            log?.Invoke($"    {"apply",-24} FAILED — {merge.ErrorMessage}");
            return;
        }

        result.Replaced = merge.Replaced;
        result.Added = merge.Added;
        result.Kept = merge.Total - merge.Added - merge.Replaced;
        log?.Invoke($"    {"applied",-24} {merge.Replaced} replaced, {merge.Added} added, {result.Kept} untouched");

        // Only the images that came from the donor. Run over the whole file it
        // would also inline the mip chains of everything the swap never touched
        // — the target's boots, cap and team textures — rewriting resources
        // that were supposed to come through untouched, and bloating the file
        // to do it. Theirs still resolve against the sidecars they always did.
        var strip = await Erp.StripMipsAsync(outputPath, gameDir, mipFilter, cancel: cancel);
        if (!strip.Succeeded) result.Warnings.Add($"Embedding the mipmaps failed: {strip.ErrorMessage}");
        else log?.Invoke($"    {"mipmaps",-24} embedded for the new images");

        result.OutputPath = outputPath;
        result.OutputResources = merge.Total;
        log?.Invoke($"    {Path.GetFileName(outputPath),-24} {merge.Total} resources");
    }

    /// <summary>"charles_leclerc" reads as "Charles Leclerc".</summary>
    private static string Pretty(string token) => string.Join(' ', token
        .Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>
    /// Empty the working folder so a run never mixes with the last one's output.
    /// Recreated rather than deleted outright, so a folder open in Explorer
    /// doesn't make the run fail.
    /// </summary>
    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try { File.Delete(file); } catch { /* the run overwrites what it needs */ }
            }
        }
        Directory.CreateDirectory(path);
    }
}
