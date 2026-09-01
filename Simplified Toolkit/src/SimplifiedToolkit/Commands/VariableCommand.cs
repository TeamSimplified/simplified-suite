using System.CommandLine;
using SimplifiedToolkit.Core.Config;

namespace SimplifiedToolkit.Cli.Commands;

/// <summary>
/// Persistent user variables stored under <c>%APPDATA%/erp-handler/config.json</c>.
/// Once defined, references like <c>{NAME}</c> in any command argument are
/// expanded automatically before parsing.
/// </summary>
internal static class VariableCommand
{
    public static Command Build()
    {
        return new Command("variable", "Persistent variables substituted as {name} in any command")
        {
            BuildDefine(),
            BuildDelete(),
            BuildList(),
            BuildGet(),
        };
    }

    private static Command BuildDefine()
    {
        var nameArg  = new Argument<string>("variable_name") { Description = "Variable name (letters/digits/underscore; must start with letter or underscore)" };
        var valueArg = new Argument<string>("variable_value") { Description = "Value (use quotes if it contains spaces)" };
        var cmd = new Command("define", "Create or update a variable") { nameArg, valueArg };
        cmd.SetAction(p =>
        {
            var name = p.GetValue(nameArg)!;
            if (!IsValidName(name))
            {
                Console.Error.WriteLine($"Invalid variable name: '{name}'. Names must match [A-Za-z_][A-Za-z0-9_]*.");
                return 1;
            }
            var value = p.GetValue(valueArg)!;
            var cfg = UserConfig.Load();
            var old = cfg.Variables.TryGetValue(name, out var prev) ? prev : null;
            cfg.Variables[name] = value;
            cfg.Save();
            if (old is null)
                Console.WriteLine($"defined  {name} = {value}");
            else
                Console.WriteLine($"updated  {name}: {old}  ->  {value}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildDelete()
    {
        var nameArg = new Argument<string>("variable_name") { Description = "Variable to remove" };
        var cmd = new Command("delete", "Remove a variable") { nameArg };
        cmd.SetAction(p =>
        {
            var name = p.GetValue(nameArg)!;
            var cfg = UserConfig.Load();
            if (cfg.Variables.Remove(name))
            {
                cfg.Save();
                Console.WriteLine($"deleted  {name}");
                return 0;
            }
            Console.Error.WriteLine($"no such variable: {name}");
            return 1;
        });
        return cmd;
    }

    private static Command BuildList()
    {
        var cmd = new Command("list", "Show all defined variables");
        cmd.SetAction(_ =>
        {
            var vars = UserConfig.Load().Variables;
            if (vars.Count == 0)
            {
                Console.WriteLine("(no variables defined)");
                return 0;
            }
            var width = vars.Keys.Max(k => k.Length);
            foreach (var kv in vars.OrderBy(k => k.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {kv.Key.PadRight(width)}  =  {kv.Value}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildGet()
    {
        var nameArg = new Argument<string>("variable_name");
        var cmd = new Command("get", "Print the value of one variable") { nameArg };
        cmd.SetAction(p =>
        {
            var name = p.GetValue(nameArg)!;
            var vars = UserConfig.Load().Variables;
            if (vars.TryGetValue(name, out var val))
            {
                Console.WriteLine(val);
                return 0;
            }
            Console.Error.WriteLine($"no such variable: {name}");
            return 1;
        });
        return cmd;
    }

    private static bool IsValidName(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s.AsSpan(1))
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
}
