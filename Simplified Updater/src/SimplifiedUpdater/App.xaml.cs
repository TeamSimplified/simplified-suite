using System.IO;
using System.Windows;
using SimplifiedUpdater.Core.Config;
using SimplifiedUpdater.Core.Mod;

namespace SimplifiedUpdater;

public partial class App : Application
{
    /// <summary>
    /// The code the process reports when a person closed the window.
    ///
    /// Nothing in here shuts the app down on its own, so a normal exit means
    /// someone shut it. That is the difference the release smoke check needs:
    /// it starts the packaged exe and asks whether it is still up, and a build
    /// that quit by itself would otherwise report the same 0 that success
    /// reports.
    /// </summary>
    public const int ClosedByUserExitCode = 1000;

    /// <summary>
    /// A mod Explorer handed over, if any — from "Open with" on an archive.
    /// The window picks it up once it is on screen.
    ///
    /// Matched against the extensions this app actually reads, so an unrelated
    /// argument is ignored rather than guessed at. The app claims no file type
    /// of its own: .zip belongs to whatever the user has set, and taking it
    /// would be rude.
    /// </summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFile = e.Args.FirstOrDefault(a => ModArchive.LooksLikeMod(a) && File.Exists(a));

        // The saved palette, before the first window is built, so the app never
        // flashes the wrong theme on the way up.
        try
        {
            SimplifiedUi.ThemeManager.Apply(UserConfig.Load().Theme);
        }
        catch
        {
            // An unreadable config is not a reason to refuse to start; the
            // default palette is already merged.
        }

        // The game folder, the first time the app is run. Every app here needs
        // one and every one of them used to open with the field empty, so the
        // first thing anyone did was go to Settings and press Detect. Only when
        // nothing is saved: a folder the user chose is never second-guessed.
        try
        {
            var config = UserConfig.Load();
            if (Simplified.Shared.GameDirDetector.DetectIfUnset(config.GameDir) is { } found)
            {
                config.GameDir = found;
                config.Save();
            }
        }
        catch
        {
            // Nothing here is required to start; Settings still has Detect.
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        e.ApplicationExitCode = ClosedByUserExitCode;
        base.OnExit(e);
    }
}
