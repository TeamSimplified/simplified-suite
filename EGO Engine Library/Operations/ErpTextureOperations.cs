using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;

namespace EgoEngineLibrary.Operations;

/// <summary>
/// Bulk texture export and import, lifted out of the <c>textures</c> command so
/// anything in the suite can drive it without starting a process. Same bodies,
/// same messages - the console writes became a <c>log</c> callback and the
/// counters became a return value.
/// </summary>
public static class ErpTextureOperations
{
    public sealed record ExportResult(int Exported, int Skipped, int Failed);
    public sealed record ImportResult(int Imported, int Skipped, int Missing, int Failed);

    /// <param name="slice">A single array layer to take as a plain 2D image, or -1 for the whole array.</param>
    /// <param name="flat">Dump everything into one folder, disambiguating names that would collide.</param>
    public static ExportResult Export(
        ErpFile erp, string outDir, bool png = false, bool withMips = false, string? gameDir = null,
        bool flat = false, string? filter = null, int slice = -1,
        TexconvRunner? runner = null, Action<string>? log = null)
    {
        if (png) runner ??= new TexconvRunner();

        // For flat output, precompute disambiguated names: if two textures share
        // a basename, prefix each with its immediate parent folder.
        var rx = string.IsNullOrEmpty(filter)
            ? null
            : new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var textureRes = erp.Resources
            .Where(r => r.ResourceType == "GfxSRVResource")
            .Where(r => rx is null || rx.IsMatch(r.Identifier))
            .ToList();

        Dictionary<ErpResource, string>? flatNames = null;
        if (flat)
        {
            flatNames = new Dictionary<ErpResource, string>(textureRes.Count);
            var collisions = textureRes
                .GroupBy(r => r.FileName.Replace("?", "%3F"), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .ToHashSet();
            foreach (var r in textureRes)
            {
                var safe = r.FileName.Replace("?", "%3F");
                if (collisions.Contains(r))
                {
                    var parent = Path.GetFileName(r.Folder.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) safe = $"{parent}__{safe}";
                }
                flatNames[r] = safe;
            }
        }

        int ok = 0, fail = 0, skip = 0;
        foreach (var res in textureRes)
        {
            try
            {
                var srv = new ErpGfxSRVResource();
                srv.FromResource(res);

                if (png && !TexconvRunner.IsSupportedForPngConversion(srv.SurfaceRes.Fragment0.ImageType))
                {
                    log?.Invoke($"SKIP {res.FileName} ({srv.SurfaceRes.Fragment0.ImageType} not supported for --png)");
                    skip++;
                    continue;
                }

                // A single slice is a plain 2D image. Skip resources without that layer.
                var arraySize = srv.SurfaceRes.Fragment0.ArraySize;
                var singleSlice = slice >= 0;
                if (singleSlice && slice >= arraySize)
                {
                    log?.Invoke($"SKIP {res.FileName} (slice {slice} out of range; array size {arraySize})");
                    skip++;
                    continue;
                }

                string folder, baseName;
                if (flat)
                {
                    folder = outDir;
                    baseName = flatNames![res];
                }
                else
                {
                    folder = Path.Combine(outDir, res.Folder);
                    Directory.CreateDirectory(folder);
                    baseName = res.FileName.Replace("?", "%3F");
                }
                // Tag the slice index into the filename so per-slice exports
                // don't collide and so a per-slice import finds the match.
                if (singleSlice) baseName += $".slice{slice}";
                var ddsPath = Path.Combine(folder, baseName + ".dds");

                Stream? mipStream = null;
                if (withMips && srv.SurfaceRes.HasMips && srv.SurfaceRes.HasValidMips)
                {
                    var mipFile = Path.Combine(gameDir!, srv.SurfaceRes.Frag2.MipMapFileName);
                    if (File.Exists(mipFile))
                        mipStream = File.Open(mipFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                }

                DdsFile dds;
                using (mipStream)
                    dds = singleSlice
                        ? srv.ToDdsFile(mipStream, exportTexArray: false, texArrayIndex: (uint)slice)
                        : srv.ToDdsFile(mipStream, exportTexArray: true, texArrayIndex: 0);

                using (var fs = File.Open(ddsPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    dds.Write(fs, -1);

                if (png && runner is not null)
                {
                    runner.DdsToPng(ddsPath, folder,
                        isSrgb: TexconvRunner.IsSrgbFormat(srv.SurfaceRes.Fragment0.ImageType));
                    File.Delete(ddsPath);
                }
                log?.Invoke($"OK   {res.FileName}");
                ok++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"FAIL {res.FileName} — {ex.Message}");
                fail++;
            }
        }
        return new ExportResult(ok, skip, fail);
    }

    /// <param name="mipsOut">Where new <c>.mipmaps</c> go when no game directory is given.</param>
    public static ImportResult Import(
        ErpFile erp, string inDir, bool png = false, string? gameDir = null, string? mipsOut = null,
        int slice = -1, TexconvRunner? runner = null, Action<string>? log = null)
    {
        if (png) runner ??= new TexconvRunner();
        mipsOut ??= inDir.TrimEnd('\\', '/') + "_mipmaps";
        var tmpRoot = Path.Combine(Path.GetTempPath(), "erp-handler", Guid.NewGuid().ToString("N"));

        int ok = 0, miss = 0, fail = 0, skip = 0;
        try
        {
            foreach (var res in erp.Resources.Where(r => r.ResourceType == "GfxSRVResource"))
            {
                try
                {
                    var srv = new ErpGfxSRVResource();
                    srv.FromResource(res);

                    var singleSlice = slice >= 0;
                    if (singleSlice && slice >= srv.SurfaceRes.Fragment0.ArraySize)
                    {
                        log?.Invoke($"SKIP {res.FileName} (slice {slice} out of range; array size {srv.SurfaceRes.Fragment0.ArraySize})");
                        skip++;
                        continue;
                    }

                    var ext = png ? ".png" : ".dds";
                    // Per-slice exports are tagged "<name>.sliceN" — match the same name on import.
                    var sliceTag = singleSlice ? $".slice{slice}" : "";
                    var fname = res.FileName.Replace("?", "%3F") + sliceTag + ext;
                    var folder = Path.Combine(inDir, res.Folder);
                    var inputPath = Path.Combine(folder, fname);
                    if (!File.Exists(inputPath)) { miss++; continue; }

                    if (png && !TexconvRunner.IsSupportedForPngConversion(srv.SurfaceRes.Fragment0.ImageType))
                    {
                        log?.Invoke($"SKIP {res.FileName} ({srv.SurfaceRes.Fragment0.ImageType} not supported for --png)");
                        skip++;
                        continue;
                    }

                    // PNG → DDS via texconv (target format from the existing texture).
                    string ddsPath = inputPath;
                    (uint Width, uint Height)? resizedFrom = null;
                    if (png && runner is not null)
                    {
                        Directory.CreateDirectory(tmpRoot);
                        // Match the existing chain length so the same depth is regenerated.
                        var mipCount = srv.SurfaceRes.HasMips
                            ? (int)(srv.SurfaceRes.Fragment0.MipMapCount + srv.SurfaceRes.Frag2.Mips.Count)
                            : (int)srv.SurfaceRes.Fragment0.MipMapCount;

                        // One slice of a tex array occupies a fixed stretch of
                        // the resource, so the encoded DDS has to come out at the
                        // array's exact size. This route re-encodes anyway, so it
                        // is told to land there rather than the import being
                        // refused for a canvas that grew. An ordinary texture may
                        // change size freely and is left alone.
                        var f0 = srv.SurfaceRes.Fragment0;
                        var fitToSlice = singleSlice && f0.ArraySize > 1;
                        if (fitToSlice)
                        {
                            if (ReadPngSize(inputPath) is { } arrived
                                && (arrived.Width != f0.Width || arrived.Height != f0.Height))
                            {
                                resizedFrom = arrived;
                            }
                            ddsPath = runner.PngToDds(inputPath, tmpRoot, f0.ImageType,
                                Math.Max(mipCount, 1), width: f0.Width, height: f0.Height);
                        }
                        else
                        {
                            ddsPath = runner.PngToDds(inputPath, tmpRoot, f0.ImageType, Math.Max(mipCount, 1));
                        }
                    }

                    // Decide where the .mipmaps sidecar lands.
                    Stream? mipStream = null;
                    if (srv.SurfaceRes.HasMips)
                    {
                        string mipsSavePath;
                        if (!string.IsNullOrEmpty(gameDir))
                            mipsSavePath = Path.Combine(gameDir, srv.SurfaceRes.Frag2.MipMapFileName);
                        else
                            mipsSavePath = Path.Combine(mipsOut, res.Folder, fname + ".mipmaps");
                        Directory.CreateDirectory(Path.GetDirectoryName(mipsSavePath)!);
                        mipStream = File.Open(mipsSavePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    }

                    using (var fs = File.Open(ddsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var dds = new DdsFile(fs);
                        using (mipStream)
                            dds.ToErpGfxSRVResource(srv, mipStream!,
                                importTexArray: !singleSlice,
                                texArrayIndex: singleSlice ? (uint)slice : 0);
                    }

                    srv.ToResource(res);
                    // Say when the file was fitted to the slice, so a batch run
                    // does not quietly put back a different image than the one
                    // on disk.
                    log?.Invoke($"OK   {res.FileName}" + (resizedFrom is { } from
                        ? $" (resized from {from.Width}x{from.Height})"
                        : ""));
                    ok++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"FAIL {res.FileName} — {ex.Message}");
                    fail++;
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); } catch { }
        }

        return new ImportResult(ok, skip, miss, fail);
    }

    /// <summary>
    /// The pixel size in a PNG's IHDR, read from the first 24 bytes so the
    /// image never has to be decoded just to say whether it was resized. Null
    /// if the file is not a PNG or is unreadable — in which case nothing is
    /// claimed about it.
    /// </summary>
    private static (uint Width, uint Height)? ReadPngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;

            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (!head[..8].SequenceEqual(signature)) return null;

            return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[16..20]),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[20..24]));
        }
        catch
        {
            return null;
        }
    }
}
