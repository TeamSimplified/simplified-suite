using System.CommandLine;
using EgoEngineLibrary.Operations;
using EgoEngineLibrary.Archive.Erp;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using SimplifiedToolkit.Core.Config;
using EgoEngineLibrary.Util;

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
            var result = ErpOperations.StripMips(erp, gameDir, filter, Console.WriteLine);

            erp.UpdateOffsets();
            ErpFileIO.Save(erp, outPath);

            Console.WriteLine($"{result.Embedded} embedded, {result.AlreadyInline} already inline, " +
                              $"{result.Unreachable} unreachable, {result.Failed} failed");
            Console.WriteLine($"Wrote {outPath}");
            return result.Failed > 0 ? 1 : 0;
        });

        return cmd;
    }
}
