using System;

namespace SimplifiedUniversalExporter.Core;

/// <summary>
/// Where the engine's progress lines go. The window plugs its activity log in;
/// with nothing plugged in — the command line, a closed window — a line is
/// simply dropped, so a worker thread that outlives the window can't fail on it.
/// </summary>
public static class ExporterLog
{
    public static Action<string> Sink { get; set; }

    public static void Write(string message)
    {
        try { Sink?.Invoke(message); }
        catch { /* logging never stops the work */ }
    }
}
