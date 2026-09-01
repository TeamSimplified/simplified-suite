using System.CommandLine;
using SimplifiedToolkit.Core.Config;

namespace SimplifiedToolkit.Cli.Commands;

internal static class ConfigCommand
{
    public static Command Build()
    {
        var keyArg = new Argument<string>("key") { Description = "Config key (currently only: game-dir)" };
        var valArg = new Argument<string>("value") { Description = "Value to set" };

        var setCmd = new Command("set", "Set a config value") { keyArg, valArg };
        setCmd.SetAction(p =>
        {
            var key = p.GetValue(keyArg)!;
            var val = p.GetValue(valArg)!;
            var cfg = UserConfig.Load();
            switch (key)
            {
                case "game-dir":
                    cfg.GameDir = val;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown config key: {key}");
                    return 1;
            }
            cfg.Save();
            Console.WriteLine($"Saved {key} = {val}");
            Console.WriteLine($"  ({UserConfig.ConfigPath})");
            return 0;
        });

        var getCmd = new Command("get", "Get a config value") { keyArg };
        getCmd.SetAction(p =>
        {
            var key = p.GetValue(keyArg)!;
            var cfg = UserConfig.Load();
            string? val = key switch
            {
                "game-dir" => cfg.GameDir,
                _ => null,
            };
            if (val is null)
            {
                Console.Error.WriteLine($"Unknown or unset config key: {key}");
                return 1;
            }
            Console.WriteLine(val);
            return 0;
        });

        var unsetCmd = new Command("unset", "Unset a config value") { keyArg };
        unsetCmd.SetAction(p =>
        {
            var key = p.GetValue(keyArg)!;
            var cfg = UserConfig.Load();
            switch (key)
            {
                case "game-dir":
                    cfg.GameDir = null;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown config key: {key}");
                    return 1;
            }
            cfg.Save();
            Console.WriteLine($"Unset {key}");
            return 0;
        });

        return new Command("config", "Manage user config (stored under %APPDATA%)") { setCmd, getCmd, unsetCmd };
    }
}
