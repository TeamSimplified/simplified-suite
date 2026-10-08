using System;
using System.Windows;
using System.Windows.Input;
using SimplifiedSponsorEditor.Core.Project;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// Saving the project, and saying so.
/// </summary>
public partial class ShellWindow
{
    /// <summary>
    /// Whether anything has happened since the last save.
    ///
    /// Only what the file would hold: a sponsor added, changed or deleted, and
    /// the project\'s own name. What is half-typed on a page nobody has ticked
    /// is not in the project yet and is not what the question is about.
    /// </summary>
    private bool _dirty;

    /// <summary>How long SAVED stays up before the word goes back to STATUS.</summary>
    private static readonly TimeSpan Said = TimeSpan.FromSeconds(1.6);

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
        // Escape backs out of a page that has a cross on it, and only those:
        // elsewhere it would be a shortcut for nothing, and a key that does
        // nothing on most pages is worse than one that is not bound. The
        // cross\'s own tooltip says Esc, so the two have to agree.
        if (e.Key == Key.Escape && _stage is Stage.Naming or Stage.Sponsor)
        {
            e.Handled = true;

            if (_stage == Stage.Sponsor) CancelSponsor();
            else CancelNaming();

            return;
        }

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
    /// <param name="announce">
    /// Whether to say so on the heading. False where the caller is already
    /// retitling: two retitles at once animate the same property, and the
    /// second would replace the first outright.
    /// </param>
    private bool SaveProject(bool announce = true)
    {
        if (_project is not { } project) return false;

        try
        {
            // The sponsors are written into the working tree first; Save is what
            // zips that tree. The other order would save the project and then
            // change it, which would mean the file on disk never had the last
            // sponsor in it.
            SponsorFile.Write(project, _sponsors);
            ProjectStore.Save(project);

            _dirty = false;
            if (announce) Announce();
        }
        catch (Exception problem)
        {
            MessageDialog.Show(this,
                $"The project couldn't be saved.\n\n{problem.Message}",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Ask about unsaved work before <paramref name="action"/>, and say whether
    /// to go ahead. The suite\'s question, with the same three answers it asks
    /// everywhere else \u2014 buttons that say what they do, so the body does not
    /// have to gloss Yes and No.
    /// </summary>
    private bool ConfirmDiscardChanges(string action)
    {
        if (_project is not { } project || !_dirty) return true;

        var answer = MessageDialog.Choose(this,
            "Unsaved changes",
            $"{(string.IsNullOrWhiteSpace(project.Info.Name) ? project.DisplayName : project.Info.Name)} "
            + "has changes that haven\u2019t been saved.",
            $"Save writes them into the project first. Discard throws them away. "
            + $"Either way the app will then {action}.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(announce: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>
    /// Say it on the heading, and put the heading back after a moment.
    ///
    /// The heading is the status — it is what says SETTING UP YOUR PROJECT
    /// while one is being made. STATUS is the little grey word that labels it,
    /// and changing a label to SAVED says the heading is now called "saved",
    /// which is not a thing that happened.
    ///
    /// Chained through <c>then</c> rather than scheduled: both retitles animate
    /// the heading\'s opacity, so a second one on a timer would replace the
    /// first and the words would never arrive.
    /// </summary>
    private void Announce()
    {
        // Only where there is a heading to give back. Mid-transition the page
        // is already retitling itself and this would be a third voice.
        if (_stage != Stage.Project) return;

        Retitle("PROJECT", "SAVED.", then: () =>
            After(Said.TotalSeconds, () =>
            {
                if (_stage == Stage.Project)
                    Retitle(ProjectTitle.First, ProjectTitle.Second);
            }));
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

        // Just read off disk, so the window and the file agree.
        _dirty = false;

        if (_sponsors.Count == 0)
        {
            // Nothing to show, so nothing to make room for. A project that was
            // just named left the block at the top; this puts it back.
            Shelf.Visibility = Visibility.Collapsed;
            Shelf.IsHitTestVisible = false;
            Lower();
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
