using System.Windows;

namespace SimplifiedDriverwearSwapper;

public partial class App : Application
{
    /// <summary>
    /// The code the process reports when a person closed the window.
    ///
    /// Nothing in here shuts the app down on its own, so a normal exit means
    /// someone shut it. That is the difference the release smoke check needs:
    /// it starts the packaged exe and asks whether it is still up, and a build
    /// that quit by itself would otherwise report the same 0 that success
    /// reports. The release runs on the same machine somebody is working on,
    /// so windows do get closed while it waits.
    /// </summary>
    public const int ClosedByUserExitCode = 1000;

    protected override void OnExit(ExitEventArgs e)
    {
        e.ApplicationExitCode = ClosedByUserExitCode;
        base.OnExit(e);
    }
}
