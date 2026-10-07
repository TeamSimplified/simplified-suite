using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SimplifiedSponsorEditor.Core.Project;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The row along the bottom of the project page, and the corner beside it.
///
/// They belong to the project rather than to the window: there is nothing to
/// save, rename or package until one is open, and nothing to do to one while a
/// sponsor is being edited — that page has a tick, and a Save beside it would be
/// a second answer to the same question.
/// </summary>
public partial class ShellWindow
{
    /// <summary>
    /// Whether the naming page was opened to change a project rather than to
    /// make one. The same page either way; the difference is what the tick does
    /// at the end of it.
    /// </summary>
    private bool _renaming;

    /// <summary>
    /// Bring the tools in, or take them away. One call rather than six, because
    /// they arrive and leave together and the window has enough staging in it
    /// already.
    /// </summary>
    private void ShowTools(bool wanted, double delay = 0)
    {
        Tools.IsHitTestVisible = wanted;
        SettingsButton.IsHitTestVisible = wanted;
        ProjectTag.IsHitTestVisible = wanted;

        // The cross goes back under the name whichever way this is going. Left
        // out, a pointer resting on the tag while the page changed would leave
        // it hanging there over a page that has no project in it.
        if (!wanted) Reveal(false);

        if (wanted)
        {
            FadeInAfter(Tools, delay, PlusIn);
            FadeInAfter(SettingsButton, delay, PlusIn);
            FadeInAfter(ProjectTag, delay, PlusIn);
            return;
        }

        Fade(Tools, to: 0, seconds: ClearSeconds);
        Fade(SettingsButton, to: 0, seconds: ClearSeconds);
        Fade(ProjectTag, to: 0, seconds: ClearSeconds);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();

    /// <summary>
    /// Rename the project: the naming page again, filled in with what it is
    /// called now. The tick writes it back rather than asking for a file,
    /// because this project already has one.
    /// </summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project || _stage != Stage.Project) return;

        _renaming = true;
        _stage = Stage.Naming;

        ProjectName.Text = project.Info.Name;
        ProjectAuthor.Text = project.Info.Author;

        ShowTools(false);
        Shelf.IsHitTestVisible = false;
        Fade(Shelf, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        Lift();

        Retitle("NAME YOUR", "PROJECT", delay: ClearSeconds, then: () =>
        {
            Shelf.Visibility = Visibility.Collapsed;

            Naming.Visibility = Visibility.Visible;
            Naming.IsHitTestVisible = true;
            FadeInAfter(Naming, delay: 0, seconds: PlusIn);

            ActionGlyph.Data = Geometry.Parse(TickData);
            AddButton.ToolTip = "Keep the changes";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);

            ProjectName.Focus();
            ProjectName.SelectAll();
        });
    }

    /// <summary>
    /// The tick on the naming page when it was opened to rename. Writes the two
    /// fields into the project and saves it where it already lives — no dialog,
    /// because nobody asked to move the file.
    /// </summary>
    private void Rename()
    {
        _renaming = false;

        if (_project is { } project)
        {
            project.Info.Name = ProjectName.Text.Trim();
            project.Info.Author = ProjectAuthor.Text.Trim();
        }

        NameTheProject();

        _stage = Stage.Project;

        Naming.IsHitTestVisible = false;
        Fade(Naming, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        After(ClearSeconds + Gap, () =>
        {
            Naming.Visibility = Visibility.Collapsed;

            ActionGlyph.Data = Geometry.Parse(PlusData);
            AddButton.ToolTip = "Add a sponsor";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);

            ShowTools(true);
            BackToShelf();
        });

        Retitle(ProjectTitle.First, ProjectTitle.Second, delay: ClearSeconds + Gap);

        // Written out straight away. Somebody who renames a project and closes
        // the window has said what it is called, and finding the old name on it
        // next time would read as the rename not having worked.
        SaveProject();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_stage != Stage.Project) return;

        _stage = Stage.Settings;

        ShowTools(false);
        Shelf.IsHitTestVisible = false;
        Fade(Shelf, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        Lift();

        Retitle("YOUR", "SETTINGS", delay: ClearSeconds, then: () =>
        {
            Shelf.Visibility = Visibility.Collapsed;

            SettingsPage.Visibility = Visibility.Visible;
            SettingsPage.IsHitTestVisible = true;
            FadeInAfter(SettingsPage, delay: 0, seconds: PlusIn);

            ActionGlyph.Data = Geometry.Parse(TickData);
            AddButton.ToolTip = "Back to the project";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
        });
    }

    /// <summary>The tick on the settings page: nothing to keep, so just back.</summary>
    private void LeaveSettings()
    {
        _stage = Stage.Project;

        SettingsPage.IsHitTestVisible = false;
        Fade(SettingsPage, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        After(ClearSeconds + Gap, () =>
        {
            SettingsPage.Visibility = Visibility.Collapsed;

            ActionGlyph.Data = Geometry.Parse(PlusData);
            AddButton.ToolTip = "Add a sponsor";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);

            ShowTools(true);
            BackToShelf();
        });

        Retitle(ProjectTitle.First, ProjectTitle.Second, delay: ClearSeconds + Gap);
    }

    /// <summary>
    /// Put the shelf back as the project page has it — up with its sponsors, or
    /// away with the block in the middle when there are none.
    /// </summary>
    private void BackToShelf()
    {
        if (_sponsors.Count == 0)
        {
            Shelf.Visibility = Visibility.Collapsed;
            Drop();
            return;
        }

        Shelf.Visibility = Visibility.Visible;
        Shelf.IsHitTestVisible = true;
        FadeInAfter(Shelf, delay: 0, seconds: PlusIn);
    }
}
