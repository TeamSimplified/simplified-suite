using System.CommandLine;
using EgoEngineLibrary.Operations;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using SimplifiedToolkit.Core.Config;
using EgoEngineLibrary.Texconv;
using EgoEngineLibrary.Util;

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
            var result = ErpTextureOperations.Export(
                erp, outDir, png, withMips, gameDir, flat, filter, slice, log: Console.WriteLine);

            Console.WriteLine($"\n{result.Exported} exported, {result.Skipped} skipped, {result.Failed} failed");
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
            var result = ErpTextureOperations.Import(
                erp, inDir, png, gameDir, mipsOut, slice, log: Console.WriteLine);

            erp.UpdateOffsets();
            ErpFileIO.Save(erp, erpPath);
            Console.WriteLine($"\n{result.Imported} imported, {result.Skipped} skipped, {result.Missing} missing, {result.Failed} failed");
            return 0;
        });
        return cmd;
    }
}
