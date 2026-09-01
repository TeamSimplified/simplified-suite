using System.Text;

namespace SimplifiedToolkit.Core.Config;

/// <summary>
/// Substitutes <c>{name}</c> placeholders in CLI arguments with values stored
/// in <see cref="UserConfig.Variables"/>. Unknown variables and literal text
/// pass through unchanged. Use <c>{{</c> / <c>}}</c> for a literal brace.
///
/// Variable names: ASCII letter or underscore, then letters/digits/underscores.
/// </summary>
public static class VariableExpander
{
    public static string[] Expand(string[] args)
    {
        var vars = UserConfig.Load().Variables;
        // Fast path: no work to do
        if (vars.Count == 0 && !ArgsHaveBraces(args)) return args;

        var result = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
            result[i] = ExpandOne(args[i], vars);
        return result;
    }

    private static bool ArgsHaveBraces(string[] args)
    {
        foreach (var a in args)
            if (a.IndexOf('{') >= 0 || a.IndexOf('}') >= 0) return true;
        return false;
    }

    public static string ExpandOne(string input, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.IndexOf('{') < 0 && input.IndexOf('}') < 0) return input;

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            var c = input[i];

            if (c == '{')
            {
                // Escaped {{
                if (i + 1 < input.Length && input[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }
                // Try to parse {NAME}
                if (TryReadName(input, i + 1, out var name, out var afterClose))
                {
                    if (vars.TryGetValue(name, out var value))
                    {
                        sb.Append(value);
                    }
                    else
                    {
                        // Unknown var — preserve raw token
                        sb.Append(input, i, afterClose - i);
                    }
                    i = afterClose;
                    continue;
                }
                // Lone { — emit and move on
                sb.Append('{');
                i++;
                continue;
            }

            if (c == '}')
            {
                if (i + 1 < input.Length && input[i + 1] == '}')
                {
                    sb.Append('}');
                    i += 2;
                    continue;
                }
                sb.Append('}');
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryReadName(string s, int start, out string name, out int afterClose)
    {
        name = "";
        afterClose = start;
        if (start >= s.Length) return false;
        var first = s[start];
        if (!(char.IsAsciiLetter(first) || first == '_')) return false;

        int j = start + 1;
        while (j < s.Length)
        {
            var ch = s[j];
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') j++;
            else break;
        }
        if (j >= s.Length || s[j] != '}') return false;
        name = s.Substring(start, j - start);
        afterClose = j + 1;
        return true;
    }
}
