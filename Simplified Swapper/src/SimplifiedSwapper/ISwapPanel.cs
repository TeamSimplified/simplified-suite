using SimplifiedSwapper.Core.Config;

namespace SimplifiedSwapper;

/// <summary>How a status line should read.</summary>
public enum SwapStatus
{
    /// <summary>Ordinary progress or an instruction.</summary>
    Plain,
    /// <summary>Something is wrong, or finished with warnings.</summary>
    Problem,
    /// <summary>Finished cleanly.</summary>
    Done,
}

/// <summary>
/// What the window needs from a swap panel.
///
/// An interface rather than a shared base class because each panel is a XAML
/// UserControl and its root element decides what it derives from — this crosses
/// the two without either giving up being what it is.
/// </summary>
public interface ISwapPanel
{
    /// <summary>
    /// Hand the panel the app's settings and the template host, and let it
    /// start. Separate from the constructor because a UserControl is built by
    /// XAML with no arguments.
    /// </summary>
    void Initialise(UserConfig config, ISerpsHost host);

    /// <summary>Re-read the settings. <paramref name="gameDirChanged"/> means reload from the game.</summary>
    void SettingsChanged(bool gameDirChanged);

    /// <summary>
    /// Put every choice back to how the panel starts.
    ///
    /// Called when the template goes away, because the choices were made for
    /// that template: leaving a chassis and a rename filled in from the last
    /// one is how the wrong thing gets swapped into the next.
    /// </summary>
    void Reset();

    /// <summary>
    /// Whether this panel has been moved off what the open template already is.
    ///
    /// A template opens showing its own team on both sides, which is the state
    /// of not having asked for anything. Swap runs the panels that have been
    /// changed and leaves the rest alone, so opening a template and pressing
    /// Swap does nothing rather than rebuilding a car as itself.
    /// </summary>
    bool IsChanged { get; }

    /// <summary>Whether a swap could start right now, if it were asked for.</summary>
    bool CanSwap { get; }

    /// <summary>Raised when either of those changes, so the window can follow.</summary>
    event EventHandler? CanSwapChanged;

    /// <summary>
    /// Run this panel's swap. Awaitable so the window can run one after another
    /// rather than starting both into the same template at once.
    /// </summary>
    Task RunSwapAsync();
}
