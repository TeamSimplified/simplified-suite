using System.Windows;
using SimplifiedTrackInstaller.Core.Config;

namespace SimplifiedTrackInstaller;

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

    protected override void OnStartup(StartupEventArgs e)
    {
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

        // The game folder, the first time the app is run. Every package is
        // built from the game's own files, so opening with the field empty
        // would make the first action "go and find your install" every time.
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
