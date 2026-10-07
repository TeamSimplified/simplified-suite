using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SimplifiedSponsorEditor.Core.Project;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Saving the project, and saying so.
/// </summary>
public partial class ShellWindow
{
    /// <summary>How long SAVED stays up before the word goes back to STATUS.</summary>
    private static readonly TimeSpan Said = TimeSpan.FromSeconds(1.6);

    private DispatcherTimer? _saying;

    /// <summary>
    /// Ctrl+S, from anywhere in the window.
    ///
    /// PreviewKeyDown rather than an InputBinding: the name and the bio are text
    /// boxes, and that is where the cursor is when somebody thinks to save. A
    /// KeyBinding on the window would be reached only if the focused control let
    /// the key past, and a TextBox does not have to.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;
        SaveProject();
    }

    /// <summary>
    /// Write the project where it already lives.
    ///
    /// There is always somewhere: New saves as part of making one and Open came
    /// from a file, so a project in hand has a path. Nothing to save yet is not
    /// an error — Ctrl+S on the landing page should do nothing rather than
    /// complain about a project nobody has started.
    /// </summary>
    private void SaveProject()
    {
        if (_project is not { } project) return;

        try
        {
            // The sponsors are written into the working tree first; Save is what
            // zips that tree. The other order would save the project and then
            // change it, which would mean the file on disk never had the last
            // sponsor in it.
            SponsorFile.Write(project, _sponsors);
            ProjectStore.Save(project);
            Say("SAVED");
        }
        catch (Exception problem)
        {
            MessageDialog.Show(this,
                $"The project couldn't be saved.\n\n{problem.Message}",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Put a word where STATUS goes, and let it fall back after a moment.
    ///
    /// The heading is left alone. It says what the page is for, which has not
    /// changed because something was saved, and borrowing it would mean the page
    /// appearing to navigate somewhere on every Ctrl+S.
    /// </summary>
    private void Say(string word)
    {
        _saying?.Stop();

        StatusWord.Text = word;

        _saying = new DispatcherTimer { Interval = Said };

        _saying.Tick += (_, _) =>
        {
            _saying?.Stop();
            _saying = null;
            StatusWord.Text = "STATUS";
        };

        _saying.Start();
    }

    /// <summary>
    /// Put the project's sponsors on the shelf. Called once a project is in
    /// hand, so opening one that has some shows them rather than an empty page.
    /// </summary>
    private void LoadSponsors()
    {
        if (_project is not { } project) return;

        _sponsors.Clear();
        _sponsors.AddRange(SponsorFile.Read(project));
        Reshelve();

        if (_sponsors.Count == 0)
        {
            // Nothing to show, so nothing to make room for. A project that was
            // just named left the block at the top; this puts it back.
            Shelf.Visibility = Visibility.Collapsed;
            Shelf.IsHitTestVisible = false;
            Drop();
            return;
        }

        // A project with sponsors in it wants its shelf up — which means the
        // block has to be at the top for the shelf to sit under.
        Lift();
        Shelf.Visibility = Visibility.Visible;
        Shelf.IsHitTestVisible = true;
        FadeInAfter(Shelf, delay: Gap, seconds: PlusIn);
    }
}
