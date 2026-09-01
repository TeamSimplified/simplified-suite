using System.CommandLine;
using SimplifiedToolkit.Cli.Commands;
using SimplifiedToolkit.Core.Config;

namespace SimplifiedToolkit.Cli;

internal static class CliRunner
{
    /// <summary>
    /// Build the root command tree and run it. Used by Program.cs and by
    /// <see cref="CommandSequence"/> for each line of a `run` script.
    /// Expands <c>{name}</c> variable references in args before parsing,
    /// except for <c>variable define</c> where the value is taken verbatim.
    /// </summary>
    public static int Run(string[] args)
    {
        args = MaybeExpandVariables(args);

        // All ERP subcommands live under `stk erp ...`
        var erp = new Command("erp", "Codemasters Ego Engine resource package operations")
        {
            ListCommand.Build(),
            ExtractCommand.Build(),
            RepackCommand.Build(),
            MergeCommand.Build(),
            ResourceCommand.Build(),
            FragmentCommand.Build(),
            TexturesCommand.Build(),
            StripMipsCommand.Build(),
            XmlCommand.Build(),
            PkgCommand.Build(),
            ConfigCommand.Build(),
        };

        var root = new RootCommand("Simplified Toolkit — ERP / LNG / file utilities")
        {
            erp,
            LngCommand.Build(),
            FileCommand.Build(),
            VariableCommand.Build(),
            RunCommand.Build(),
        };
        return root.Parse(args).Invoke();
    }

    /// <summary>
    /// Apply variable substitution to args — but keep the <c>variable</c>
    /// subcommand's arguments raw so that values containing <c>{x}</c> can be
    /// stored as literals.
    /// </summary>
    private static string[] MaybeExpandVariables(string[] args)
    {
        if (args.Length >= 1 && string.Equals(args[0], "variable", StringComparison.Ordinal))
            return args;
        return VariableExpander.Expand(args);
    }
}
