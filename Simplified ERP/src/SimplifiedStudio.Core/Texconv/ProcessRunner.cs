using System.Diagnostics;
using System.IO;

namespace SimplifiedStudio.Core.Texconv;

/// <summary>
/// Spawns texconv.exe and streams its stdout/stderr line-by-line via callbacks.
/// </summary>
public sealed class ProcessRunner
{
    public async Task<int> RunAsync(string exePath, IList<string> args,
        Action<string> onOutput, CancellationToken ct)
    {
        if (!File.Exists(exePath))
        {
            onOutput($"texconv not found at: {exePath}");
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onOutput(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return -2;
        }
        catch (Exception ex)
        {
            onOutput($"!! launch failed: {ex.Message}");
            return -3;
        }
    }
}
