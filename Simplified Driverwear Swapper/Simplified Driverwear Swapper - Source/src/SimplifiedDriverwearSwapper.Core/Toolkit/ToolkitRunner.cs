using System.Diagnostics;
using System.Text;

namespace SimplifiedDriverwearSwapper.Core.Toolkit;

/// <summary>
/// Runs the Simplified Toolkit CLI (<c>stk.exe</c>), which ships beside this app.
///
/// This app deliberately owns no ERP format code: everything that reads or writes
/// a game file goes through the Toolkit, so the format lives in exactly one place
/// across the suite. When something can't be done here, the fix is to add it to
/// the Toolkit rather than to reimplement it locally.
/// </summary>
public static class ToolkitRunner
{
    /// <summary>What one <c>stk</c> invocation produced.</summary>
    /// <param name="ExitCode">Zero on success; the Toolkit returns 1 on failure.</param>
    public sealed record ToolkitResult(int ExitCode, string StandardOutput, string StandardError, string CommandLine)
    {
        public bool Succeeded => ExitCode == 0;

        /// <summary>
        /// Something short enough to show a user. Prefers stderr, falling back to
        /// the tail of stdout where the Toolkit reports refusals.
        ///
        /// An unhandled exception in the Toolkit arrives as a full stack trace, so
        /// only the message is kept — the trace stays in
        /// <see cref="StandardError"/> for the log.
        /// </summary>
        public string ErrorMessage
        {
            get
            {
                var stderr = StandardError.Trim();
                if (stderr.Length > 0) return Summarise(stderr);

                var lines = StandardOutput.Trim()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.Length > 0 ? lines[^1] : $"stk exited with code {ExitCode}.";
            }
        }

        /// <summary>
        /// "Unhandled exception: System.IO.FileNotFoundException: ERP file not
        /// found: x.erp" → "ERP file not found: x.erp".
        /// </summary>
        private static string Summarise(string errorOutput)
        {
            var first = errorOutput.Split('\n')[0].Trim();

            const string unhandled = "Unhandled exception: ";
            if (first.StartsWith(unhandled, StringComparison.Ordinal)) first = first[unhandled.Length..];

            // Drop a leading "Some.Namespace.SomeException: " if one is present.
            var match = System.Text.RegularExpressions.Regex.Match(
                first, @"^(?:[A-Za-z0-9_]+\.)*[A-Za-z0-9_]*Exception:\s*(?<message>.+)$");
            return match.Success ? match.Groups["message"].Value.Trim() : first;
        }
    }

    private static string? _cachedPath;

    /// <summary>
    /// Full path to the bundled <c>stk.exe</c>, or null when it's missing.
    ///
    /// It ships loose next to the app rather than embedded, because a single-file
    /// build can't execute an exe it hasn't extracted. Falls back to PATH so a
    /// developer running from the IDE still works.
    /// </summary>
    public static string? FindToolkit()
    {
        if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "tools", "stk.exe"),
            Path.Combine(baseDir, "stk.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            _cachedPath = candidate;
            return candidate;
        }

        var onPath = FindOnPath("stk.exe");
        if (onPath is not null) _cachedPath = onPath;
        return onPath;
    }

    /// <summary>The Toolkit's version string, or null when it can't be run.</summary>
    public static async Task<string?> GetVersionAsync(CancellationToken cancel = default)
    {
        try
        {
            var result = await RunAsync(new[] { "--version" }, cancel: cancel);
            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Run <c>stk</c> with the given arguments.
    ///
    /// Arguments are passed as a list rather than a formatted string so paths
    /// containing spaces — which every game path does — are quoted by the
    /// runtime instead of by hand.
    /// </summary>
    /// <param name="onOutput">Called per line as output arrives, for live logging.</param>
    public static async Task<ToolkitResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onOutput = null,
        CancellationToken cancel = default)
    {
        var exe = FindToolkit()
                  ?? throw new FileNotFoundException(
                      "stk.exe wasn't found next to the app. Reinstall so the tools folder is present.");

        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onOutput?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var commandLine = $"stk {string.Join(' ', arguments.Select(Quote))}";
        return new ToolkitResult(process.ExitCode, stdout.ToString(), stderr.ToString(), commandLine);
    }

    /// <summary>Quote an argument for display only — never for building a command line.</summary>
    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), fileName); }
            catch { continue; }   // PATH entries can hold invalid characters
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
