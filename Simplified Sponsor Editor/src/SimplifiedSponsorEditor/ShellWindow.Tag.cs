using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SimplifiedSponsorEditor.Core.Project;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The project's name over the tools, and the cross that closes it.
/// </summary>
public partial class ShellWindow
{
    /// <summary>How long the name takes to move aside. Short: it is an answer to
    /// the pointer arriving, and an answer that takes a beat reads as a delay.</summary>
    private const double TagSlide = 0.16;

    /// <summary>Between the name and the cross once it is out.</summary>
    private const double TagGap = 6;

    /// <summary>
    /// Say what project this is. Called wherever the project changes — opening
    /// one, making one, renaming one — so the corner cannot be left saying the
    /// name of something that is no longer open.
    /// </summary>
    private void NameTheProject()
    {
        // As it was typed. This is Body now, and Body is prose — upper-casing a
        // name somebody chose would be the window correcting them.
        ProjectTagName.Text = _project is { } project
            ? (string.IsNullOrWhiteSpace(project.Info.Name)
                ? project.DisplayName
                : project.Info.Name.Trim())
            : "";
    }

    /// <summary>
    /// Wire the hover. Done once, from the constructor, because the handlers
    /// outlive every page.
    /// </summary>
    private void WatchTheTag()
    {
        ProjectTag.MouseEnter += (_, _) => Reveal(true);
        ProjectTag.MouseLeave += (_, _) => Reveal(false);
    }

    /// <summary>
    /// Slide the name aside and bring the cross out, or put both back.
    ///
    /// How far is measured off the cross rather than written down, so changing
    /// its size cannot leave the two overlapping. A render transform moves the
    /// name, not a margin: a margin would re-run the layout on every frame of a
    /// hover, and the Grid would resize under it.
    /// </summary>
    private void Reveal(bool wanted)
    {
        // Hit-testing, not opacity. Opacity was the wrong question twice over:
        // the tag fades in over PlusIn, so for the first half of that it reads
        // under a half and the MouseEnter that arrived in the window was thrown
        // away — and a pointer is only told to enter once, so it never asked
        // again. This is the flag that actually says whether the tag is live,
        // and it is true for the whole of the fade.
        if (!ProjectTag.IsHitTestVisible) return;

        var travel = wanted ? -(CloseProjectButton.Width + TagGap) : 0;

        ProjectTagShift.BeginAnimation(
            TranslateTransform.XProperty, Glide(travel, TagSlide));

        CloseProjectButton.IsHitTestVisible = wanted;
        Fade(CloseProjectButton, to: wanted ? 1 : 0, seconds: TagSlide);
    }

    /// <summary>
    /// Put the project away and go back to the landing.
    ///
    /// Asked rather than assumed. This used to save on the way out, which meant
    /// a cross in the corner silently committed whatever was on the shelf — and
    /// there was then no way to abandon an afternoon somebody regretted. The
    /// suite asks this question everywhere else and it asks it here now.
    /// </summary>
    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } project || _stage != Stage.Project) return;

        // Save, Discard or Cancel. False is Cancel, or a save that failed —
        // either way the project stays open, because closing after failing to
        // save is the one case where the save mattered most.
        if (!ConfirmDiscardChanges("close the project")) return;

        _stage = Stage.Landing;

        Reveal(false);
        ShowTools(false);
        Fade(ProjectTag, to: 0, seconds: ClearSeconds);
        ProjectTag.IsHitTestVisible = false;

        Shelf.IsHitTestVisible = false;
        Fade(Shelf, to: 0, seconds: ClearSeconds);

        AddButton.IsHitTestVisible = false;
        Fade(AddButton, to: 0, seconds: ClearSeconds);

        After(ClearSeconds + Gap, () =>
        {
            Shelf.Visibility = Visibility.Collapsed;
            Shelved.Children.Clear();
            _sponsors.Clear();
            _dirty = false;

            ProjectStore.Close(project);
            _project = null;
            NameTheProject();

            // Everything a landing page is: the choices back, the block in the
            // middle, nothing else on screen.
            ShowLanding();
        });
    }
}
