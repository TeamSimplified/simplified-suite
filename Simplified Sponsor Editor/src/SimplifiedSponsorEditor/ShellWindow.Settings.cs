using System.IO;
using System.Windows;
using Microsoft.Win32;
using SimplifiedSponsorEditor.Core.Config;
using SimplifiedSponsorEditor.Core.Package;
using SimplifiedSponsorEditor.Util;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The settings page: which build this is, where the game is, and who made the
/// app.
///
/// Three things rather than a list of switches, because this app has one setting
/// — and that one is only wanted when a mod is packaged. The page is mostly
/// saying so, which is why the prose above the row is half of it.
/// </summary>
public partial class ShellWindow
{
    /// <summary>
    /// Put the page's own values in it, before it is shown.
    ///
    /// Read each time rather than once at startup: the game folder is written
    /// down by packaging as well — it detects the install and keeps it — so a
    /// page that filled itself at launch would show nothing after the first
    /// package had found it.
    /// </summary>
    private void FillSettings()
    {
        BuildLine.Text = AppVersion.Display;
        GameDirBox.Text = UserConfig.Load().GameDir ?? "";
    }

    /// <summary>
    /// The box lost the caret, so whatever is in it is what somebody meant.
    ///
    /// Said about, not refused. A folder that is not the game's is worth
    /// mentioning at once rather than at the end of a package — but it is also
    /// what somebody has if they are halfway through finding it, so it is kept
    /// either way and the packager asks again if it has to.
    /// </summary>
    private async void GameDir_Done(object sender, RoutedEventArgs e)
    {
        var typed = GameDirBox.Text.Trim();

        var config = UserConfig.Load();
        var before = config.GameDir ?? "";

        if (string.Equals(typed, before, System.StringComparison.OrdinalIgnoreCase)) return;

        config.GameDir = typed.Length == 0 ? null : typed;
        config.Save();

        if (typed.Length == 0) return;

        if (!Directory.Exists(typed))
        {
            MessageDialog.Show(this,
                "There is no folder there yet, so packaging will ask again.",
                "Game directory", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        if (!Directory.Exists(Path.Combine(typed, SponsorPackager.ShaderFolderName)))
        {
            MessageDialog.Show(this,
                $"There is no {SponsorPackager.ShaderFolderName} folder in there, so this is not "
                + "the game's own folder. It is the one F1_25.exe sits in.",
                "Game directory", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        // A different game is a different build, so what came out of the last
        // one is no longer the installed game's. This is also the one way a
        // first run with no game set ever gets the text at all.
        await EnsureGameFiles();
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var current = GameDirBox.Text.Trim();

        var picker = new OpenFolderDialog
        {
            Title = "Where F1 25 is installed",
            InitialDirectory = Directory.Exists(current) ? current : "",
        };

        if (picker.ShowDialog(this) != true) return;

        GameDirBox.Text = picker.FolderName;

        // Through the same path a typed one takes, so a chosen folder is
        // checked and written down by the one piece of code that does either.
        GameDir_Done(sender, e);
    }
}
