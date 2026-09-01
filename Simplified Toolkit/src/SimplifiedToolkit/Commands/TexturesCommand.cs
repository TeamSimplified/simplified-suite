using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using SimplifiedToolkit.Core.Config;
using SimplifiedToolkit.Core.Texconv;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

internal static class TexturesCommand
{
    public static Command Build()
    {
        return new Command("textures", "Bulk export/import textures as DDS (or PNG with --png)")
        {
            BuildExport(),
            BuildImport(),
        };
    }

    private static Command BuildExport()
    {
        var fileArg     = new Argument<string>("file");
        var dirArg      = new Argument<string>("outDir");
        var pngOpt      = new Option<bool>("--png") { Description = "Convert to PNG via texconv (BC6H/HDR refused)" };
        var mipsOpt     = new Option<bool>("--with-mips") { Description = "Include external high-res mipmaps (needs game-dir)" };
        var gameDirOpt  = new Option<string?>("--game-dir") { Description = "Game install root used to resolve .mipmaps files" };
        var flatOpt     = new Option<bool>("--flat") { Description = "Dump all textures into outDir directly (no folder structure). Collisions get a parent-folder prefix." };
        var filterOpt   = new Option<string?>("--filter") { Description = "Regex match against the resource identifier (e.g. 'ferrari')" };
        var sliceOpt    = new Option<int>("--slice") { Description = "Export only one slice of a texture array as a plain 2D image (0-based). Default -1 = whole array.", DefaultValueFactory = _ => -1 };

        var cmd = new Command("export", "Export every GfxSRVResource texture")
        {
            fileArg, dirArg, pngOpt, mipsOpt, gameDirOpt, flatOpt, filterOpt, sliceOpt
        };
        cmd.SetAction(p =>
        {
            var erpPath = p.GetValue(fileArg)!;
            var outDir = p.GetValue(dirArg)!;
            var png = p.GetValue(pngOpt);
            var withMips = p.GetValue(mipsOpt);
            var flat = p.GetValue(flatOpt);
            var filter = p.GetValue(filterOpt);
            var slice = p.GetValue(sliceOpt);
            var gameDir = UserConfig.ResolveGameDir(p.GetValue(gameDirOpt));

            if (withMips && string.IsNullOrEmpty(gameDir))
            {
                Console.Error.WriteLine(
                    "--with-mips requires a game directory. " +
                    "Set one via --game-dir, ERP_GAME_DIR env var, or `erp config set game-dir <path>`.");
                return 1;
            }

            var erp = ErpFileIO.Load(erpPath);
            Directory.CreateDirectory(outDir);
            var runner = png ? new TexconvRunner() : null;

            // For --flat, precompute disambiguated names: if two textures share a basename,
            // prefix each with its immediate parent folder.
            var rx = string.IsNullOrEmpty(filter) ? null : new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
                        if (!string.IsNullOrEmpty(parent))
                            safe = $"{parent}__{safe}";
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
                        Console.WriteLine($"SKIP {res.FileName} ({srv.SurfaceRes.Fragment0.ImageType} not supported for --png)");
                        skip++;
                        continue;
                    }

                    // --slice picks a single array layer to export as a plain 2D
                    // image. Skip resources that don't have that layer.
                    var arraySize = srv.SurfaceRes.Fragment0.ArraySize;
                    var singleSlice = slice >= 0;
                    if (singleSlice && slice >= arraySize)
                    {
                        Console.WriteLine($"SKIP {res.FileName} (slice {slice} out of range; array size {arraySize})");
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
                    // don't collide and so `import --slice N` finds the match.
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
                    Console.WriteLine($"OK   {res.FileName}");
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL {res.FileName} — {ex.Message}");
                    fail++;
                }
            }
            Console.WriteLine($"\n{ok} exported, {skip} skipped, {fail} failed");
            return 0;
        });
        return cmd;
    }

    private static Command BuildImport()
    {
        var fileArg    = new Argument<string>("file");
        var dirArg     = new Argument<string>("inDir");
        var pngOpt     = new Option<bool>("--png") { Description = "Inputs are PNG; converted to DDS via texconv using each texture's existing ERP format" };
        var gameDirOpt = new Option<string?>("--game-dir") { Description = "If set, write .mipmaps directly into the game directory" };
        var mipsOutOpt = new Option<string?>("--mips-out") { Description = "Where to write new .mipmaps files (default: <inDir>_mipmaps/)" };
        var sliceOpt   = new Option<int>("--slice") { Description = "Import a single 2D image into one slice of a texture array (0-based), leaving other slices intact. Looks for <name>.sliceN.<ext>. Default -1 = whole array.", DefaultValueFactory = _ => -1 };

        var cmd = new Command("import", "Re-import textures and rebuild fragments / external mipmaps")
        {
            fileArg, dirArg, pngOpt, gameDirOpt, mipsOutOpt, sliceOpt
        };
        cmd.SetAction(p =>
        {
            var erpPath = p.GetValue(fileArg)!;
            var inDir = p.GetValue(dirArg)!;
            var png = p.GetValue(pngOpt);
            var slice = p.GetValue(sliceOpt);
            var gameDir = UserConfig.ResolveGameDir(p.GetValue(gameDirOpt));
            var mipsOut = p.GetValue(mipsOutOpt) ?? (inDir.TrimEnd('\\', '/') + "_mipmaps");

            var erp = ErpFileIO.Load(erpPath);
            var runner = png ? new TexconvRunner() : null;
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
                            Console.WriteLine($"SKIP {res.FileName} (slice {slice} out of range; array size {srv.SurfaceRes.Fragment0.ArraySize})");
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
                            Console.WriteLine($"SKIP {res.FileName} ({srv.SurfaceRes.Fragment0.ImageType} not supported for --png)");
                            skip++;
                            continue;
                        }

                        // PNG → DDS via texconv (target format from existing ERP texture)
                        string ddsPath = inputPath;
                        (uint Width, uint Height)? resizedFrom = null;
                        if (png && runner is not null)
                        {
                            Directory.CreateDirectory(tmpRoot);
                            // Mip count: match the existing chain length so we regenerate the same depth
                            var mipCount = srv.SurfaceRes.HasMips
                                ? (int)(srv.SurfaceRes.Fragment0.MipMapCount + srv.SurfaceRes.Frag2.Mips.Count)
                                : (int)srv.SurfaceRes.Fragment0.MipMapCount;

                            // One slice of a tex array occupies a fixed stretch
                            // of the resource, so the encoded DDS has to come
                            // out at the array's exact size. This route
                            // re-encodes anyway, so it is told to land there
                            // rather than the import being refused for a canvas
                            // that grew. An ordinary texture may change size
                            // freely and is left alone.
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

                        // Decide where the .mipmaps sidecar lands
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
                        // Say when the file was fitted to the slice, so a batch
                        // run does not quietly put back a different image than
                        // the one on disk.
                        Console.WriteLine($"OK   {res.FileName}" + (resizedFrom is { } from
                            ? $" (resized from {from.Width}x{from.Height})"
                            : ""));
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FAIL {res.FileName} — {ex.Message}");
                        fail++;
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); } catch { }
            }

            erp.UpdateOffsets();
            ErpFileIO.Save(erp, erpPath);
            Console.WriteLine($"\n{ok} imported, {skip} skipped, {miss} missing, {fail} failed");
            return 0;
        });
        return cmd;
    }

    /// <summary>
    /// The pixel size in a PNG's IHDR, read from the first 24 bytes so the
    /// image never has to be decoded just to say whether it was resized.
    /// Null if the file is not a PNG or is unreadable — in which case nothing
    /// is claimed about it.
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
