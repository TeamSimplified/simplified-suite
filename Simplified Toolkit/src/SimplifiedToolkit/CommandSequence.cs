using System.Text;

namespace SimplifiedToolkit.Cli;

/// <summary>
/// Shared sequential-execution engine for <c>erp batch</c> (file-driven) and
/// <c>erp run</c> (inline). Tokenizes each command string, dispatches via
/// <see cref="CliRunner.Run"/>, tracks success/failure, supports --stop-on-error.
/// </summary>
internal static class CommandSequence
{
    public static int Execute(IEnumerable<string> commands, bool stopOnError, string lineLabel = "line")
    {
        int total = 0, ok = 0, fail = 0;
        int i = 0;
        foreach (var raw in commands)
        {
            i++;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            total++;
            var tokens = Tokenize(trimmed);
            if (tokens.Length == 0) continue;

            Console.WriteLine($"\n--[ {lineLabel} {i} ]-- {trimmed}");
            int rc;
            try
            {
                rc = CliRunner.Run(tokens);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ! exception: {ex.Message}");
                rc = 1;
            }

            if (rc == 0) ok++;
            else
            {
                fail++;
                Console.Error.WriteLine($"  ! {lineLabel} {i} exited with code {rc}");
                if (stopOnError)
                {
                    Console.Error.WriteLine($"\n--stop-on-error: aborting.");
                    Console.WriteLine($"\nSummary: {ok}/{total} succeeded, {fail} failed");
                    return rc;
                }
            }
        }
        Console.WriteLine($"\nSummary: {ok}/{total} succeeded, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Minimal shell-style tokenizer: splits on whitespace, honors single and
    /// double quotes. Inside <c>"..."</c> only <c>\"</c> and <c>\\</c> are
    /// escape sequences — every other backslash passes through, so Windows
    /// paths like <c>C:\Users\foo</c> work naturally.
    /// </summary>
    public static string[] Tokenize(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else if (c == '\\' && quote == '"' && i + 1 < input.Length
                         && (input[i + 1] == '"' || input[i + 1] == '\\'))
                {
                    sb.Append(input[++i]);
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens.ToArray();
    }
}
