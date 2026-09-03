using System.Text.RegularExpressions;
using SimplifiedHelmetSwapper.Core.Toolkit;

namespace SimplifiedHelmetSwapper.Core.Helmet;

/// <summary>
/// Builds one driver's helmet out of another's.
///
/// The whole helmet moves, not just its paint: the generic shell it is built on,
/// the driver's own attachments, the four materials and the images behind them.
/// Afterwards the target is wearing the donor's helmet, down to the shape.
///
/// Every step is a Toolkit call and every rename is a rename of resource
/// <i>identifiers</i>, never of bytes inside a payload — which is what lets two
/// drivers with names of different lengths swap at all. The pieces that keep the
/// donor's names do so on purpose; see <see cref="RunAsync"/>.
/// </summary>
public static class HelmetPrepare
{
    public sealed class PrepareOptions
    {
        public required string GameDir { get; init; }

        /// <summary>The helmet being taken.</summary>
        public required HelmetCatalogue.Entry Donor { get; init; }

        /// <summary>The driver who will wear it.</summary>
        public required HelmetCatalogue.Entry Target { get; init; }

        /// <summary>Where the extracts land. Emptied first, so each run starts clean.</summary>
        public required string WorkingDir { get; init; }

        /// <summary>The single ERP everything is merged into.</summary>
        public required string OutputPath { get; init; }
    }

    public sealed class PrepareResult
    {
        public string WorkingDir { get; init; } = "";
        public List<string> Outputs { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Families actually carried over, and ones the donor didn't have.</summary>
        public List<string> Families { get; } = new();
        public List<string> MissingFamilies { get; } = new();

        /// <summary>What the donor's helmet is built on, and what the target's was.</summary>
        public string DonorShell { get; set; } = "";
        public string TargetShell { get; set; } = "";
        public bool ShellChanged => !DonorShell.Equals(TargetShell, StringComparison.OrdinalIgnoreCase);

        public string? OutputPath { get; set; }
        public int OutputResources { get; set; }
    }

    private const string PartsDir = "Parts";

    /// <summary>
    /// Take the donor's helmet apart, re-address the pieces, and put them back
    /// together as the target's.
    ///
    /// Two pieces deliberately keep the donor's identifiers. The attachment LODs
    /// stay because the model that references them was renamed but its payload
    /// was not, so the names it points at have to still exist — the driverwear
    /// swap ships its LODs the same way for the same reason. The textures stay
    /// because the materials naming them are the donor's, and shipping both
    /// unchanged is what makes patching bytes unnecessary.
    /// </summary>
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
        log?.Invoke(result.ShellChanged
            ? $"    shell               {donor.Shell} replacing {target.Shell}"
            : $"    shell               {donor.Shell}, the same on both");
        log?.Invoke("");

        var dRoot = $"drivers/{d.Gender}/{d.Folder}/";
        var tRoot = $"drivers/{t.Gender}/{t.Folder}/";

        // ── The shell ───────────────────────────────────────────────────────
        // Re-addressed onto the target's shell name rather than added beside it.
        // Every driver's helmet ships its own copy of these identifiers, so they
        // resolve per helmet rather than game-wide; taking the target's names
        // means exactly one shell answers for this driver instead of two.
        await BuildAsync(d.ErpPath, Path.Combine(parts, "1_shell.erp"),
            $@"kitpart_assets/generic/helmets/{Regex.Escape(donor.Shell)}/",
            [(donor.Shell, target.Shell), ($"?context={d.Folder}", $"?context={t.Folder}")],
            "shell", result, log, cancel);

        // ── The attachments the driver wears on it ──────────────────────────
        var attach = $"{Regex.Escape(d.Driver)}_helmet_attachments";

        await BuildAsync(d.ErpPath, Path.Combine(parts, "2_attachments.erp"),
            $@"{Regex.Escape(dRoot)}idf/{attach}\.idf\?(model|render)$",
            [(dRoot, tRoot), ($"{d.Driver}_helmet_attachments", $"{t.Driver}_helmet_attachments"),
             ($"?context={d.Folder}", $"?context={t.Folder}")],
            "attachments", result, log, cancel);

        // Their geometry, under the donor's names — see the summary above.
        await BuildAsync(d.ErpPath, Path.Combine(parts, "3_attachment_lods.erp"),
            $@"{Regex.Escape(dRoot)}idf/{attach}\.idf\?lod=",
            [], "attachment lods", result, log, cancel);

        // ── The four materials, and the images they name ────────────────────
        var families = HelmetCatalogue.HelmetFamilies.Where(f => donor.Families.Contains(f)).ToList();
        result.Families.AddRange(families);
        result.MissingFamilies.AddRange(HelmetCatalogue.HelmetFamilies.Except(families));

        if (families.Count == 0)
            result.Warnings.Add($"{d.Label} has no helmet textures of their own, so there is nothing to take.");

        var group = string.Join('|', families.Select(Regex.Escape));

        if (families.Count > 0)
        {
            // Only the folder moves. A material is called the same thing for
            // everyone, and its payload still names the donor's images — which
            // is why they ship next to it, unrenamed.
            await BuildAsync(d.ErpPath, Path.Combine(parts, "4_materials.erp"),
                $@"{Regex.Escape(dRoot)}materials/({group})\.material$",
                [(dRoot, tRoot)], "materials", result, log, cancel);

            await BuildAsync(d.ErpPath, Path.Combine(parts, "5_textures.erp"),
                $@"{Regex.Escape(dRoot)}textures/{Regex.Escape(d.Driver)}_({group})_[dns]\.tif",
                [], "textures", result, log, cancel);
        }

        // A family the target has and the donor doesn't would leave the target's
        // own showing through, which is not the helmet they asked for.
        foreach (var f in target.Families.Except(families))
            result.Warnings.Add(
                $"{d.Label} has no {f.Replace('_', ' ')} of their own, so {t.Driver.Replace('_', ' ')} keeps theirs.");

        await MergeAsync(parts, options.OutputPath, options.GameDir, result, log, cancel);
        return result;
    }

    /// <summary>
    /// Pull one piece out of the donor and re-address it.
    ///
    /// An extract that matches nothing is a warning rather than a failure: the
    /// helmets are not uniform — most drivers have four texture families, some
    /// have two — and one absent piece shouldn't lose the rest of the helmet.
    /// </summary>
    private static async Task BuildAsync(
        string sourceErp, string destination, string filter,
        (string Find, string Replace)[] renames,
        string label, PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        var extract = await Erp.ExtractToErpAsync(sourceErp, destination, filter, cancel);
        if (!extract.Succeeded || !File.Exists(destination))
        {
            result.Warnings.Add($"{label}: {extract.ErrorMessage}");
            log?.Invoke($"    {label,-19} nothing matched");
            return;
        }

        foreach (var (find, replace) in renames)
        {
            if (find == replace) continue;
            var run = await Erp.RenameAllAsync(destination, find, replace, cancel);
            if (run.Succeeded) continue;

            result.Warnings.Add($"{label}: renaming '{find}' failed: {run.ErrorMessage}");
            log?.Invoke($"    {label,-19} FAILED — {run.ErrorMessage}");
            return;
        }

        result.Outputs.Add($"{PartsDir}/{Path.GetFileName(destination)}");
        log?.Invoke($"    {label,-19} {extract.Taken} resources" +
                    (renames.Length > 0 ? ", re-addressed" : ", under the donor's names"));
    }

    /// <summary>
    /// Merge the pieces into the finished helmet and pull in the external mip
    /// chains, so the file carries its own images rather than depending on
    /// sidecars that belong to the donor.
    /// </summary>
    private static async Task MergeAsync(
        string partsDir, string outputPath, string gameDir,
        PrepareResult result, Action<string>? log, CancellationToken cancel)
    {
        log?.Invoke("");

        var pieces = Directory.Exists(partsDir)
            ? Directory.GetFiles(partsDir, "*.erp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (pieces.Count == 0)
        {
            result.Warnings.Add("Nothing was extracted, so no helmet was written.");
            log?.Invoke("    (nothing to merge)");
            return;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

        var merge = await Erp.MergeAsync(pieces[0], pieces.Skip(1), outputPath, cancel: cancel);
        if (!merge.Succeeded)
        {
            result.Warnings.Add($"Merging the helmet failed: {merge.ErrorMessage}");
            log?.Invoke($"    merge               FAILED — {merge.ErrorMessage}");
            return;
        }

        // The pieces hold disjoint resources by construction, so a skip means
        // two of them claimed one identifier and it is worth hearing about.
        if (merge.Skipped > 0)
            result.Warnings.Add($"{merge.Skipped} resource(s) appeared in more than one piece.");

        var strip = await Erp.StripMipsAsync(outputPath, gameDir, cancel: cancel);
        if (!strip.Succeeded) result.Warnings.Add($"Embedding the mipmaps failed: {strip.ErrorMessage}");
        else log?.Invoke("    mipmaps             embedded");

        result.OutputPath = outputPath;
        result.OutputResources = merge.Total;
        log?.Invoke($"    {Path.GetFileName(outputPath)} — {merge.Total} resources");
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
