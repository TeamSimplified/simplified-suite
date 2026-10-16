using System.Diagnostics;
using System.IO;
using Simplified.Language;

namespace SimplifiedLauncher.Mods;

/// <summary>
/// Starting the game the ordinary way, and knowing when it has gone.
///
/// The same way on both shops: the game's exe, started from the game's own
/// folder. Each shop takes it from there — Steam closes a copy started outside
/// it and starts the game again through itself, and the EA App signs in — so
/// the game comes up as its shop starts it without the launcher knowing either
/// one's way of being asked. Nothing is run elevated and nothing but the game
/// is started.
///
/// Whether it is up is a question about processes, because neither shop says:
/// the game is running while a process with the exe's name is. The same way
/// the launcher before this one told, and for the same reason — Steam's own
/// answer is a URI that returns immediately whatever happens next.
/// </summary>
public static class GameLaunch
{
    /// <summary>
    /// How long a launch may take to bring the game up before it is given up
    /// on. Long on purpose: a first start can sit behind a Steam update, a
    /// shader cache being built, or the anti-cheat installing itself.
    /// </summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What else counts as the game being up: EA's anti-cheat launcher, which
    /// the game's exe hands over to and which starts the game proper.
    ///
    /// Measured on a launch: the exe appears, hands over and closes, and the
    /// game comes up again a while later. Watching the exe alone, that gap read
    /// as the game being closed and the game was put back under a launch that
    /// was still under way.
    /// </summary>
    private static readonly string[] Launchers = ["EAAntiCheat.GameServiceLauncher"];

    /// <summary>
    /// How long the game has to have been up before it counts as properly
    /// started rather than still being handed from one process to the next.
    /// </summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long everything has to stay gone before the game counts as closed,
    /// before it has properly started. Long, because the gaps in a start —
    /// Steam closing a copy started outside it and starting its own, the
    /// anti-cheat handing over — have nothing on screen to say they are gaps.
    /// </summary>
    private static readonly TimeSpan GoneWhileStarting = TimeSpan.FromSeconds(60);

    /// <summary>The same once it has properly started, when a gap means somebody quit.</summary>
    private static readonly TimeSpan GoneOnceSettled = TimeSpan.FromSeconds(10);

    /// <summary>The game's process name — the exe's, without the extension.</summary>
    private static string ProcessName(string? gameDir) =>
        Path.GetFileNameWithoutExtension(LanguageFiles.FindExe(gameDir)) is { Length: > 0 } name ? name : "F1_25";

    /// <summary>Whether the game's own exe is running now.</summary>
    public static bool IsRunning(string? gameDir) => Any(ProcessName(gameDir));

    /// <summary>Whether the game or anything starting it is running now.</summary>
    private static bool InSession(string? gameDir) => IsRunning(gameDir) || Launchers.Any(Any);

    private static bool Any(string name)
    {
        try
        {
            var found = Process.GetProcessesByName(name);
            foreach (var process in found) process.Dispose();
            return found.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start the game's exe. Returns why not, or null when it was started —
    /// which says nothing yet about whether the game comes up.
    /// </summary>
    public static string? Start(string? gameDir)
    {
        try
        {
            var exe = LanguageFiles.FindExe(gameDir);
            if (exe is null) return "The game's exe isn't in the game folder.";

            // Started directly rather than through the shell. The shell adds
            // nothing for an exe that is not a document, and is the slow way
            // to start one this size.
            using var _ = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            });
            return null;
        }
        catch (Exception e)
        {
            return $"The game couldn't be started. ({e.Message})";
        }
    }

    /// <summary>
    /// Wait for the game to come up. True once it, or the anti-cheat
    /// launcher starting it, is running; false when it
    /// has not appeared within <see cref="StartTimeout"/>, or on cancellation.
    /// </summary>
    public static async Task<bool> WaitForStart(string? gameDir, CancellationToken cancel = default)
    {
        var deadline = DateTime.UtcNow + StartTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (InSession(gameDir)) return true;

            try { await Task.Delay(TimeSpan.FromSeconds(1), cancel); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    /// <summary>
    /// Wait for the game to close, however long a session takes.
    ///
    /// Closed means neither the game nor the anti-cheat launcher has been
    /// running for a while: a minute while the game is still starting, ten
    /// seconds once it has been up for half a minute. Looked for every couple
    /// of seconds rather than waited on, because the anti-cheat protects the
    /// game's process and it cannot always be opened even to be waited on —
    /// and two seconds of polling costs nothing beside the game.
    /// </summary>
    public static async Task WaitForClose(string? gameDir, CancellationToken cancel = default)
    {
        var poll = TimeSpan.FromSeconds(2);
        DateTime? upSince = null;
        DateTime? goneSince = null;
        var settled = false;

        while (!cancel.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            if (IsRunning(gameDir))
            {
                upSince ??= now;
                if (now - upSince >= Settled) settled = true;
            }
            else
            {
                upSince = null;
            }

            if (InSession(gameDir))
            {
                goneSince = null;
            }
            else
            {
                goneSince ??= now;
                if (now - goneSince >= (settled ? GoneOnceSettled : GoneWhileStarting)) return;
            }

            try { await Task.Delay(poll, cancel); }
            catch (OperationCanceledException) { return; }
        }
    }
}
