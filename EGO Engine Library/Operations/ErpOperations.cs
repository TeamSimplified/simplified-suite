using System.Text.RegularExpressions;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Formats.Erp;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

namespace EgoEngineLibrary.Operations;

/// <summary>
/// What the <c>stk erp</c> commands do, as methods.
///
/// The logic used to live inside the command handlers, which meant the only way
/// to reach it was to start a process and read its console output. That is what
/// the Driverwear Swapper did: roughly 230ms of process startup per call, and a
/// regex over human-readable text standing in for a return value.
///
/// So the bodies moved here unchanged and the commands became what they should
/// have been - argument parsing, a call, and printing. Every line the CLI used
/// to write it still writes, through <paramref name="log"/>; callers that want
/// the numbers read them off the result instead.
/// </summary>
public static class ErpOperations
{
    // ── list ──────────────────────────────────────────────────────────

    /// <summary>One resource, as <c>list</c> reports it.</summary>
    public sealed record Listed(int Index, string ResourceType, int FragmentCount, ulong PackedSize, string Identifier);

    public sealed record ListResult(int Version, int Total, IReadOnlyList<Listed> Shown);

    /// <summary>
    /// The resources in an archive, optionally narrowed by resource type or a
    /// regex over the identifier. Both filters are applied here so a caller
    /// never has to pull an archive's whole table back to discard most of it.
    /// </summary>
    public static ListResult List(ErpFile erp, string? type = null, string? filter = null)
    {
        var rx = filter is null ? null : new Regex(filter, RegexOptions.IgnoreCase);
        var matched = erp.Resources
            .Where(r => type is null || string.Equals(r.ResourceType, type, StringComparison.OrdinalIgnoreCase))
            .Where(r => rx is null || rx.IsMatch(r.Identifier))
            .Select((r, i) => new Listed(i, r.ResourceType, r.Fragments.Count, r.PackedSize, r.Identifier))
            .ToList();

        return new ListResult(erp.Version, erp.Resources.Count, matched);
    }

    // ── extract ───────────────────────────────────────────────────────

    /// <summary>Unpack every fragment of every matching resource to disk.</summary>
    public static void ExtractToFolder(ErpFile erp, string outDir, string filter = "", Action<string>? log = null)
    {
        Directory.CreateDirectory(outDir);
        var exporter = new ErpResourceExporter();
        exporter.Export(erp, outDir, log is null ? null : new Progress<string>(log), null, filter);
    }

    public sealed record ExtractResult(int Taken, int OutOf);

    /// <summary>
    /// Copy the resources whose identifier matches into a new ERP, rather than
    /// unpacking their fragments to loose files. Lets a subset be lifted out of
    /// an archive in one pass, which otherwise takes one resource-remove per
    /// resource you don't want.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No filter, which would just copy the file, or nothing matched.
    /// </exception>
    public static ExtractResult ExtractToErp(ErpFile source, string outPath, string filter)
    {
        if (string.IsNullOrEmpty(filter))
            throw new ArgumentException("Extracting to an ERP needs a filter; extracting everything would just copy the file.", nameof(filter));

        var regex = new Regex(filter);

        // Same version as the source: the offset maths in UpdateOffsets is
        // version-dependent, so a mismatch would write a subtly wrong archive.
        var output = new ErpFile { Version = source.Version };

        foreach (var resource in source.Resources)
        {
            if (!regex.IsMatch(resource.Identifier)) continue;

            // Repoint to the new file before adding, so its size maths uses the
            // output's version rather than the source's.
            resource.ParentFile = output;
            foreach (var fragment in resource.Fragments) fragment.ParentFile = output;
            output.Resources.Add(resource);
        }

        if (output.Resources.Count == 0)
            throw new ArgumentException("Nothing matched — no ERP written.", nameof(filter));

        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        output.UpdateOffsets();
        ErpFileIO.Save(output, outPath);

        return new ExtractResult(output.Resources.Count, source.Resources.Count);
    }

    // ── merge ─────────────────────────────────────────────────────────

    public sealed record MergeTally(string Source, int Added, int Replaced, int Skipped);

    /// <summary>
    /// Fold one or more source archives into <paramref name="target"/>, applied
    /// left to right. <paramref name="overwrite"/> decides who wins when both
    /// hold the same identifier.
    ///
    /// <paramref name="include"/> narrows what is taken from each source, by
    /// identifier. Without it every resource comes across, which is what a plain
    /// merge means; with it one pass lifts a chosen set out of many archives at
    /// once. A resource it turns down is not counted as skipped — skipped means
    /// wanted but already there.
    /// </summary>
    public static List<MergeTally> Merge(ErpFile target, IEnumerable<string> sourcePaths, bool overwrite,
                                         Func<string, bool>? include = null)
    {
        // Indexed rather than searched. This used to be a List.Find per source
        // resource, which is fine for merging a mod into an archive and
        // quadratic for the job this predicate exists to serve: lifting tens of
        // thousands of resources out of a whole game install. First-wins on the
        // way in, matching what Find returned when a target already held two of
        // the same identifier.
        var byIdentifier = new Dictionary<string, ErpResource>(target.Resources.Count);
        foreach (var res in target.Resources) byIdentifier.TryAdd(res.Identifier, res);

        var tallies = new List<MergeTally>();
        foreach (var srcPath in sourcePaths)
        {
            var src = ErpFileIO.Load(srcPath);
            int added = 0, replaced = 0, skipped = 0;
            foreach (var res in src.Resources)
            {
                if (include is not null && !include(res.Identifier)) continue;

                if (byIdentifier.TryGetValue(res.Identifier, out var existing))
                {
                    if (overwrite)
                    {
                        target.Resources.Remove(existing);
                        RepointAndAdd(target, res);
                        byIdentifier[res.Identifier] = res;
                        replaced++;
                    }
                    else skipped++;
                }
                else
                {
                    RepointAndAdd(target, res);
                    byIdentifier[res.Identifier] = res;
                    added++;
                }
            }
            tallies.Add(new MergeTally(srcPath, added, replaced, skipped));
        }
        return tallies;
    }

    /// <summary>
    /// Repoint a merged-in resource (and its fragments) to the target ERP so
    /// that version-dependent size maths in UpdateOffsets uses the target's
    /// version. (Fixes the latent bug in the EEA GUI implementation.)
    /// </summary>
    private static void RepointAndAdd(ErpFile target, ErpResource resource)
    {
        resource.ParentFile = target;
        foreach (var frag in resource.Fragments)
            frag.ParentFile = target;
        target.Resources.Add(resource);
    }

    // ── strip-mips ────────────────────────────────────────────────────

    public sealed record StripMipsResult(int Embedded, int AlreadyInline, int Unreachable, int Failed);

    /// <summary>
    /// Pull each texture's external <c>.mipmaps</c> chain into the archive, so
    /// it carries its own full-resolution images instead of pointing at sidecar
    /// files in the game install. The mips descriptor fragment is dropped
    /// (3 fragments becomes 2) and the decoded chain written inline.
    /// </summary>
    public static StripMipsResult StripMips(ErpFile erp, string gameDir, string? filter = null, Action<string>? log = null)
    {
        var regex = filter is null ? null : new Regex(filter);

        // Snapshot first: the rewrite below mutates resources as it goes.
        var textures = erp.Resources
            .Where(r => r.ResourceType == "GfxSRVResource")
            .Where(r => regex is null || regex.IsMatch(r.Identifier))
            .ToList();

        int embedded = 0, noMips = 0, unreachable = 0, failed = 0;

        foreach (var res in textures)
        {
            try
            {
                var srv = new ErpGfxSRVResource();
                srv.FromResource(res);

                if (!srv.SurfaceRes.HasMips) { noMips++; continue; }

                var mipFileName = srv.SurfaceRes.Frag2.MipMapFileName;
                var mipPath = string.IsNullOrEmpty(mipFileName)
                    ? null
                    : Path.Combine(gameDir, mipFileName);

                if (!srv.SurfaceRes.HasValidMips || mipPath is null || !File.Exists(mipPath))
                {
                    log?.Invoke($"MISS {res.FileName}  ({mipFileName ?? "no mip file named"})");
                    unreachable++;
                    continue;
                }

                // Tex arrays have to round-trip whole: a single slice can't
                // reconstruct the array layout.
                var isArray = srv.SurfaceRes.Fragment0.ArraySize > 1;

                DdsFile dds;
                using (var mipStream = File.Open(mipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    dds = srv.ToDdsFile(mipStream, exportTexArray: isArray, texArrayIndex: 0);

                // Drop the mips descriptor from the linked surface (.image) resource.
                var surfaceRes = erp.FindResource(srv.Fragment0.SurfaceResourceName);
                if (surfaceRes.Fragments.Count == 3) surfaceRes.Fragments.RemoveAt(2);
                else if (surfaceRes.Fragments.Count != 2)
                    throw new InvalidOperationException(
                        $"expected 2 or 3 fragments on the surface resource, found {surfaceRes.Fragments.Count}");

                // Re-read so HasMips is false, then write the full chain inline.
                var newSrv = new ErpGfxSRVResource();
                newSrv.FromResource(res);
                dds.ToErpGfxSRVResource(newSrv, mipMapsStream: null!, importTexArray: isArray, texArrayIndex: 0);
                newSrv.ToResource(res);

                log?.Invoke($"OK   {res.FileName}");
                embedded++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"FAIL {res.FileName}  {ex.Message}");
                failed++;
            }
        }

        return new StripMipsResult(embedded, noMips, unreachable, failed);
    }

    // ── resource ──────────────────────────────────────────────────────

    /// <summary>Substring find/replace across every resource identifier.</summary>
    // ── remove ────────────────────────────────────────────────────────

    /// <summary>
    /// Drop every resource whose identifier matches, and say how many went.
    ///
    /// The counterpart to <see cref="ExtractToErp"/>: that one keeps what
    /// matches, this one keeps what doesn't. Removing nothing is a legitimate
    /// answer — a filter aimed at a part the archive happens not to have is a
    /// no-op, not a mistake — so unlike the extract it doesn't throw on zero.
    /// </summary>
    public static int RemoveAll(ErpFile erp, string filter, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(filter))
            throw new ArgumentException("Removing needs a filter; removing everything would just empty the file.", nameof(filter));

        var regex = new Regex(filter);
        var doomed = erp.Resources.Where(r => regex.IsMatch(r.Identifier)).ToList();

        foreach (var resource in doomed)
        {
            erp.Resources.Remove(resource);
            log?.Invoke($"removed {resource.Identifier}");
        }

        return doomed.Count;
    }

    public static int RenameAll(ErpFile erp, string find, string replace, Action<string>? log = null)
    {
        var renamed = 0;
        foreach (var r in erp.Resources)
        {
            if (!r.Identifier.Contains(find)) continue;
            var old = r.Identifier;
            r.Identifier = r.Identifier.Replace(find, replace);
            log?.Invoke($"{old}\n  → {r.Identifier}");
            renamed++;
        }
        return renamed;
    }
}
