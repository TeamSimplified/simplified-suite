using System.Windows;

namespace SimplifiedUi;

/// <summary>
/// Said before any app in the suite extracts files from the game.
///
/// What is taken out of the install becomes the base of the person's mod: it is
/// modified and packaged, and whatever the files already carry goes out with
/// it. A database or archive that an earlier mod left behind would be built
/// into their package under their name. So before the first read they are asked
/// to make sure the game is clean - in Steam, Verify integrity of game files -
/// and nothing is extracted unless they continue.
///
/// One wording for every app, so it lives here rather than in each.
/// </summary>
public static class GameFilesNotice
{
    public const string Headline = "The app is about to extract clean files from your game.";

    public const string Body =
        "These will be modified and packaged as your final mod. Before hitting continue make sure your " +
        "game files are verified and clean.";

    /// <summary>
    /// The launcher's second sentence. What it takes is not built into a mod
    /// but used as the base the chosen mods are installed into, so it says
    /// that instead; the rest is the same notice.
    /// </summary>
    public const string LauncherBody =
        "These will be used as a base to install your mods into when launching. Before hitting continue make " +
        "sure your game files are verified and clean.";

    /// <summary>True to go ahead and extract; false when the person cancelled or closed the dialog.</summary>
    public static bool Confirm(Window? owner) =>
        MessageDialog.Choose(owner, "Game files", Headline, Body,
            ("Continue", MessageBoxResult.OK),
            ("Cancel", MessageBoxResult.Cancel)) == MessageBoxResult.OK;
}
