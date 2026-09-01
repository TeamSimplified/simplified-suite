using System;

namespace SimplifiedStudio.UvEngine
{
    /// <summary>
    /// Where the engine's diagnostic lines go. The app sets
    /// <see cref="Sink"/> once so extraction and loader breadcrumbs land in its
    /// log panel; left unset they're discarded.
    /// </summary>
    public static class EngineLog
    {
        /// <summary>May be called from any thread — the sink is responsible for marshalling.</summary>
        public static Action<string> Sink;
    }
}

namespace F1modding3dtoolV1
{
    /// <summary>
    /// The engine logs through its original app's window
    /// (<c>MainWindow.LogToStatic</c>). That UI isn't here, so this same-named
    /// static stands in for it and forwards to whatever sink the app registered.
    ///
    /// It has to keep this namespace and name: the call sites are scattered
    /// through thirty thousand lines of engine source that are carried in
    /// unmodified, and a shim is a far smaller thing to maintain than a patch
    /// across all of them.
    /// </summary>
    internal static class MainWindow
    {
        public static void LogToStatic(string line)
        {
            try { SimplifiedStudio.UvEngine.EngineLog.Sink?.Invoke(line); }
            catch { /* logging must never take the caller down */ }
        }
    }
}
