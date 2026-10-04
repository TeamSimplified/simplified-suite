using System.Windows;
using SimplifiedLanguageEditor.Core.Config;

namespace SimplifiedLanguageEditor;

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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        e.ApplicationExitCode = ClosedByUserExitCode;
        base.OnExit(e);
    }
}
