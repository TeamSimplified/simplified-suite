using System.CommandLine;

namespace SimplifiedToolkit.Cli.Commands;

/// <summary>
/// Run a sequence of erp commands from a text file, one per line.
/// Blank lines and <c>#</c> comments are ignored. Each line runs to completion
/// before the next starts.
/// </summary>
internal static class RunCommand
{
    public static Command Build()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to .txt file with one command per line" };
        var stopOpt = new Option<bool>("--stop-on-error") { Description = "Abort the run on the first failing line (default: continue and report)" };

        var cmd = new Command("run", "Run a sequence of erp commands from a .txt file (one per line)")
        {
            fileArg, stopOpt
        };

        cmd.SetAction(p =>
        {
            var path = p.GetValue(fileArg)!;
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Script file not found: {path}");
                return 1;
            }
            return CommandSequence.Execute(File.ReadAllLines(path), p.GetValue(stopOpt), lineLabel: "line");
        });

        return cmd;
    }
}
