using SimplifiedUi;
using System.Configuration;
using System.Data;
using System.Windows;

namespace SimplifiedStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{

    /// <summary>
    /// Put the saved palette in place before the first window is built, so the
    /// app never flashes the wrong theme on the way up.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            ThemeManager.Apply(Core.Config.UserConfig.Load().Theme);
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
            var config = Core.Config.UserConfig.Load();
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

    /// <summary>
    /// The code the process reports when a person closed the window.
    ///
    /// Nothing in here shuts the app down on its own, so a normal exit means
    /// someone shut it. That is the difference the release smoke check needs:
    /// it starts the packaged exe and asks whether it is still up, and a build
    /// that quit by itself would otherwise report the same 0 that success
    /// reports. The release runs on the same machine somebody is working on,
    /// so windows do get closed while it waits.
    ///
    /// Measured rather than assumed: of the ways to set a WPF process's exit
    /// code, ExitEventArgs.ApplicationExitCode is the documented one and it
    /// does reach the process. WPF's own default is 0.
    /// </summary>
    public const int ClosedByUserExitCode = 1000;

    protected override void OnExit(ExitEventArgs e)
    {
        e.ApplicationExitCode = ClosedByUserExitCode;
        base.OnExit(e);
    }
}
