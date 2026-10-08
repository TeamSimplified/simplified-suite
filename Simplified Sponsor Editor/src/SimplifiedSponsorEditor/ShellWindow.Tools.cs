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
        ProjectTag.IsHitTestVisible = wanted;

        // The tools are the project page and the cross is a page you are
        // halfway through, so they are never both right. Said here rather than
        // at each of the ways back, one of which forgot — ticking a sponsor
        // returned to the project with the cross still sitting over the plus.
        if (wanted) ShowCancel(false);

        // The cross goes back under the name whichever way this is going. Left
        // out, a pointer resting on the tag while the page changed would leave
        // it hanging there over a page that has no project in it.
        if (!wanted) Reveal(false);

        if (wanted)
        {
            FadeInAfter(Tools, delay, PlusIn);
            FadeInAfter(ProjectTag, delay, PlusIn);

            // A pointer already resting in that corner when the page arrives
            // has nothing to enter, so MouseEnter never fires and the cross
            // never comes out. Ask the mouse where it is rather than waiting to
            // be told — Synchronize re-runs the hit test, which is what makes
            // IsMouseOver true for an element that only just became live.
            After(delay + PlusIn, () =>
            {
                Mouse.Synchronize();
                Reveal(ProjectTag.IsMouseOver);
            });

            return;
        }

        Fade(Tools, to: 0, seconds: ClearSeconds);
        Fade(ProjectTag, to: 0, seconds: ClearSeconds);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();

    /// <summary>Bring the cancel cross in with the naming page, or take it away.</summary>
    private void ShowCancel(bool wanted, double delay = 0)
    {
        CancelNamingButton.IsHitTestVisible = wanted;

        if (wanted) FadeInAfter(CancelNamingButton, delay, PlusIn);
        else Fade(CancelNamingButton, to: 0, seconds: ClearSeconds);
    }

    /// <summary>
    /// The cross over the tick. It appears on both pages that have a tick, and
    /// backs out of whichever one it is on.
    /// </summary>
    private void CancelNaming_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Sponsor) CancelSponsor();
        else CancelNaming();
    }

    /// <summary>
    /// Leave the sponsor page without keeping the sponsor.
    ///
    /// Everything on the page goes, whether it was a new sponsor or an old one
    /// being changed: an old one is untouched on the shelf until the tick puts
    /// it back, so abandoning the page is enough to abandon the edit.
    /// </summary>
    private void CancelSponsor()
    {
        if (_stage != Stage.Sponsor) return;

        _openAt = -1;
        _stage = Stage.Project;

        Section.IsHitTestVisible = false;
        Fade(Section, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);
        AddButton.IsHitTestVisible = false;
        ShowCancel(false);

        After(ClearSeconds + Gap, () =>
        {
            Section.Visibility = Visibility.Collapsed;

            // Emptied out of sight, as the tick empties it.
            Clear();

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
    /// Back out of the naming page without keeping anything.
    ///
    /// Where it goes back to depends on where it came from: renaming returns to
    /// the project, and naming a new one returns to the landing — there is no
    /// project yet to return to.
    /// </summary>
    private void CancelNaming()
    {
        if (_stage != Stage.Naming) return;

        var renaming = _renaming;
        _renaming = false;

        Naming.IsHitTestVisible = false;
        Fade(Naming, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);
        AddButton.IsHitTestVisible = false;
        ShowCancel(false);

        _stage = renaming ? Stage.Project : Stage.Landing;

        After(ClearSeconds + Gap, () =>
        {
            Naming.Visibility = Visibility.Collapsed;

            // Emptied once it is out of sight, so the next New opens on a blank
            // page rather than on the name somebody just abandoned.
            ProjectName.Text = "";
            ProjectAuthor.Text = "";

            ActionGlyph.Data = Geometry.Parse(PlusData);
            AddButton.ToolTip = "Add a sponsor";

            if (!renaming)
            {
                ShowLanding();
                return;
            }

            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
            ShowTools(true);
            BackToShelf();
        });

        if (renaming) Retitle(ProjectTitle.First, ProjectTitle.Second, delay: ClearSeconds + Gap);
    }

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

        Retitle("PROJECT", "DETAILS", delay: ClearSeconds, then: () =>
        {
            Shelf.Visibility = Visibility.Collapsed;

            // Behind the slide, for the reason New's are: the fields sit where
            // the heading is still travelling to.
            Naming.Visibility = Visibility.Visible;
            Naming.IsHitTestVisible = true;
            FadeInAfter(Naming, delay: SlideSeconds, seconds: PlusIn);

            ActionGlyph.Data = Geometry.Parse(TickData);
            AddButton.ToolTip = "Keep the changes";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: SlideSeconds, seconds: PlusIn);

            ShowCancel(true, delay: SlideSeconds);

            FocusWhenShown(ProjectName, SlideSeconds, selectAll: true);
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
            _dirty = true;
        }

        NameTheProject();

        _stage = Stage.Project;

        Naming.IsHitTestVisible = false;
        Fade(Naming, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);
        ShowCancel(false);

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
        //
        // Quietly: this method is already retitling, and the heading cannot say
        // two things at once.
        SaveProject(announce: false);
    }

    /// <summary>
    /// Which page settings was opened over, so leaving it goes back there.
    ///
    /// Settings is reachable from everywhere now, and "everywhere" includes two
    /// pages that are halfway through something. Nothing is lost going in — the
    /// fields are collapsed rather than cleared — but something has to remember
    /// which of them to bring back.
    /// </summary>
    private Stage _beforeSettings = Stage.Landing;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Settings) return;

        _beforeSettings = _stage;
        _stage = Stage.Settings;

        // Whatever was up goes down. Each page owns a different set of things,
        // and the one being left is the one that knows what they are.
        switch (_beforeSettings)
        {
            case Stage.Landing:
                Choices.IsHitTestVisible = false;
                Fade(NewButton, to: 0, seconds: ClearSeconds);
                Fade(OpenButton, to: 0, seconds: ClearSeconds);
                break;

            case Stage.Naming:
                Naming.IsHitTestVisible = false;
                Fade(Naming, to: 0, seconds: ClearSeconds);
                ShowCancel(false);
                break;

            case Stage.Sponsor:
                Section.IsHitTestVisible = false;
                Fade(Section, to: 0, seconds: ClearSeconds);
                ShowCancel(false);
                break;

            default:
                ShowTools(false);
                Shelf.IsHitTestVisible = false;
                Fade(Shelf, to: 0, seconds: ClearSeconds);
                break;
        }

        AddButton.IsHitTestVisible = false;
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        Lift();

        Retitle("YOUR", "SETTINGS", delay: ClearSeconds, then: () =>
        {
            Choices.Visibility = Visibility.Collapsed;
            Naming.Visibility = Visibility.Collapsed;
            Section.Visibility = Visibility.Collapsed;
            Shelf.Visibility = Visibility.Collapsed;

            SettingsPage.Visibility = Visibility.Visible;
            SettingsPage.IsHitTestVisible = true;
            FadeInAfter(SettingsPage, delay: 0, seconds: PlusIn);

            ActionGlyph.Data = Geometry.Parse(TickData);
            AddButton.ToolTip = "Back";
            AddButton.IsHitTestVisible = true;
            FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
        });
    }

    /// <summary>The tick on the settings page: nothing to keep, so just back.</summary>
    private void LeaveSettings()
    {
        var back = _beforeSettings;
        _stage = back;

        SettingsPage.IsHitTestVisible = false;
        Fade(SettingsPage, to: 0, seconds: ClearSeconds);
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        After(ClearSeconds + Gap, () =>
        {
            SettingsPage.Visibility = Visibility.Collapsed;

            switch (back)
            {
                case Stage.Landing:
                    // ShowLanding does the heading, the choices and the block,
                    // and the landing has no button at the bottom at all.
                    ShowLanding();
                    return;

                case Stage.Naming:
                    Naming.Visibility = Visibility.Visible;
                    Naming.IsHitTestVisible = true;
                    FadeInAfter(Naming, delay: 0, seconds: PlusIn);
                    ShowCancel(true);
                    Tick("Create the project");
                    return;

                case Stage.Sponsor:
                    Section.Visibility = Visibility.Visible;
                    Section.IsHitTestVisible = true;
                    FadeInAfter(Section, delay: 0, seconds: PlusIn);
                    ShowCancel(true);
                    Tick("Keep this sponsor");
                    return;

                default:
                    ActionGlyph.Data = Geometry.Parse(PlusData);
                    AddButton.ToolTip = "Add a sponsor";
                    AddButton.IsHitTestVisible = true;
                    FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
                    ShowTools(true);
                    BackToShelf();
                    return;
            }
        });

        if (back != Stage.Landing)
            Retitle(TitleFor(back).First, TitleFor(back).Second, delay: ClearSeconds + Gap);
    }

    /// <summary>Bring the button back as a tick, which is what the editing pages use.</summary>
    private void Tick(string says)
    {
        ActionGlyph.Data = Geometry.Parse(TickData);
        AddButton.ToolTip = says;
        AddButton.IsHitTestVisible = true;
        FadeInAfter(AddButton, delay: 0, seconds: PlusIn);
    }

    /// <summary>What a page's heading says, for one being returned to.</summary>
    private (string First, string Second) TitleFor(Stage stage) => stage switch
    {
        Stage.Naming => ("PROJECT", "DETAILS"),
        Stage.Sponsor => ("EDIT YOUR", "SPONSOR"),
        _ => ProjectTitle,
    };

    /// <summary>
    /// Put the shelf back as the project page has it — up with its sponsors, or
    /// away with the block in the middle when there are none.
    /// </summary>
    private void BackToShelf()
    {
        if (_sponsors.Count == 0)
        {
            Shelf.Visibility = Visibility.Collapsed;
            Lower();
            return;
        }

        Shelf.Visibility = Visibility.Visible;
        Shelf.IsHitTestVisible = true;
        FadeInAfter(Shelf, delay: 0, seconds: PlusIn);
    }
}
