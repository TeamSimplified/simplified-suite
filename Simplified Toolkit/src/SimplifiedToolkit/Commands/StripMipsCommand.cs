using System.CommandLine;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using SimplifiedToolkit.Core.Config;
using SimplifiedToolkit.Core.Util;

namespace SimplifiedToolkit.Cli.Commands;

/// <summary>
/// Pulls each texture's external <c>.mipmaps</c> chain into the ERP, so the
/// archive carries its own full-resolution images instead of pointing at sidecar
/// files in the game install.
///
/// Mechanically the mips descriptor fragment is dropped (3 fragments becomes 2)
/// and the whole decoded chain is written inline.
/// </summary>
internal static class StripMipsCommand
{
    public static Command Build()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to .erp file (modified in place)" };
        var gameDirOpt = new Option<string?>("--game-dir")
        {
            Description = "Game install root used to resolve .mipmaps files (defaults to the configured one)"
        };
        var outputOpt = new Option<string?>("--output")
        {
            Description = "Write to this path instead of overwriting the input"
        };
        var filterOpt = new Option<string?>("--filter") { Description = "Regex match against identifier" };

        var cmd = new Command("strip-mips", "Embed external .mipmaps into the ERP (3 fragments -> 2)")
        {
            fileArg, gameDirOpt, outputOpt, filterOpt
        };

        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            var outPath = p.GetValue(outputOpt) ?? path;
            var filter = p.GetValue(filterOpt);
            var gameDir = p.GetValue(gameDirOpt) ?? UserConfig.Load().GameDir;

            if (string.IsNullOrWhiteSpace(gameDir))
            {
                Console.Error.WriteLine("--game-dir is required (or set one with `stk erp config`).");
                return 1;
            }

            var erp = ErpFileIO.Load(path);
            var regex = filter is null ? null : new System.Text.RegularExpressions.Regex(filter);

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
                        Console.WriteLine($"MISS {res.FileName}  ({mipFileName ?? "no mip file named"})");
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

                    Console.WriteLine($"OK   {res.FileName}");
                    embedded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL {res.FileName}  {ex.Message}");
                    failed++;
                }
            }

            erp.UpdateOffsets();
            ErpFileIO.Save(erp, outPath);

            Console.WriteLine($"{embedded} embedded, {noMips} already inline, " +
                              $"{unreachable} unreachable, {failed} failed");
            Console.WriteLine($"Wrote {outPath}");
            return failed > 0 ? 1 : 0;
        });

        return cmd;
    }
}
