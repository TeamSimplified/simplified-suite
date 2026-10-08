using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// The sponsors in this project, as the project page shows them.
///
/// A card each: the artwork in a rounded tile with the name under it, filling
/// across the window and wrapping down. The mark and the status stay where the
/// slide put them, so adding a sponsor adds to a shelf under a heading rather
/// than replacing the page.
/// </summary>
public partial class ShellWindow
{
    private readonly List<Sponsor> _sponsors = [];

    /// <summary>
    /// What the project page's heading says, which is not one thing: an empty
    /// project is an invitation and a full one is a list. Asked for rather than
    /// written down at each of the four places that return to this page, so they
    /// cannot disagree about it.
    /// </summary>
    private (string First, string Second) ProjectTitle =>
        _sponsors.Count == 0 ? ("ADD A", "SPONSOR") : ("EDIT YOUR", "SPONSORS");

    /// <summary>Which page the window is on.</summary>
    private enum Stage
    {
        /// <summary>New and Open, with nothing started.</summary>
        Landing,

        /// <summary>Naming a project that does not exist yet.</summary>
        Naming,

        /// <summary>The project, with its sponsors on the shelf.</summary>
        Project,

        /// <summary>One sponsor, open for editing.</summary>
        Sponsor,

        /// <summary>Settings, which is a page with nothing on it yet.</summary>
        Settings,
    }

    /// <summary>
    /// Where the window is, which is what the button at the bottom finishes.
    ///
    /// Held rather than read off the glyph: the glyph is swapped at the bottom
    /// of a fade, so for a third of a second it says the opposite of what the
    /// page is doing, and a click landing in that window would do the wrong
    /// thing.
    /// </summary>
    private Stage _stage = Stage.Landing;

    /// <summary>
    /// Which sponsor is on the page, or -1 when it is a new one.
    ///
    /// The difference between keeping and adding. Without it, opening a sponsor,
    /// changing its name and ticking would leave the old one on the shelf beside
    /// the new, which is the shape of bug that looks like the app duplicating
    /// things by itself.
    /// </summary>
    private int _openAt = -1;

    /// <summary>
    /// Take what is on the sponsor page and keep it. The variations go across
    /// as they are: they are already the live list the slots were drawing, so a
    /// fresh six replaces them for whoever is edited next.
    /// </summary>
    private void Keep()
    {
        var sponsor = new Sponsor
        {
            Name = SponsorName.Text,
            Bio = SponsorBio.Text,
            Variations = [.. _variations],
        };

        if (_openAt >= 0 && _openAt < _sponsors.Count) _sponsors[_openAt] = sponsor;
        else _sponsors.Add(sponsor);

        _dirty = true;

        _openAt = -1;
        Reshelve();
    }

    /// <summary>
    /// Put a sponsor back on the page to be edited again.
    ///
    /// Filled before the page arrives rather than after. The section is still
    /// collapsed at this point, so the fields take their values out of sight —
    /// done the other way round it would be a blank page that writes itself in
    /// while somebody watches.
    /// </summary>
    private void Reopen(int at)
    {
        if (_stage == Stage.Sponsor || at < 0 || at >= _sponsors.Count) return;

        var sponsor = _sponsors[at];
        _openAt = at;

        SponsorName.Text = sponsor.Name;
        SponsorBio.Text = sponsor.Bio;

        for (var i = 0; i < _variations.Count; i++)
        {
            _variations[i] = i < sponsor.Variations.Count
                ? sponsor.Variations[i]
                : new Variation();

            Show(i);
        }

        AddButton.IsHitTestVisible = false;
        ToSponsorPage();
    }

    /// <summary>
    /// Empty the sponsor page, ready for the next one. Called once the page has
    /// faded out rather than while it is on screen, so nobody watches the fields
    /// clear themselves.
    /// </summary>
    private void Clear()
    {
        SponsorName.Text = "";
        SponsorBio.Text = "";

        for (var i = 0; i < _variations.Count; i++)
        {
            _variations[i] = new Variation();
            Show(i);
        }
    }

    /// <summary>Rebuild the shelf. Few enough of these that redrawing all beats
    /// working out which one changed.</summary>
    private void Reshelve()
    {
        Shelved.Children.Clear();

        for (var at = 0; at < _sponsors.Count; at++) Shelved.Children.Add(CardFor(at));
    }

    /// <summary>
    /// What a right-click on a card offers.
    ///
    /// Only removing it, as a variation's menu offers only emptying one — the
    /// left click already opens it, and a menu that repeats what a thing does
    /// when you click it is a longer way to do the same thing.
    ///
    /// The index is the one the card was built with, and that is safe because
    /// Reshelve rebuilds every card after a removal: the menus go with the cards
    /// they were made for, so none is left holding a position that has shifted
    /// under it.
    /// </summary>
    private ContextMenu CardMenu(int at)
    {
        var delete = new MenuItem { Header = "Delete" };

        delete.Click += (_, _) =>
        {
            if (at < 0 || at >= _sponsors.Count) return;

            _sponsors.RemoveAt(at);
            _dirty = true;

            // If the one being edited was behind this on the shelf it has just
            // moved up one, and the page is still pointing at where it used to
            // be. Nothing can reach the shelf while a sponsor is open, so this
            // cannot happen today — but the index would be silently wrong the
            // moment it could, and that is a worse thing to find later.
            if (_openAt == at) _openAt = -1;
            else if (_openAt > at) _openAt--;

            Reshelve();

            // The last one off the shelf takes the shelf with it, and the block
            // comes back down to the middle — the same page a project arrives
            // at before anything has been added to it.
            //
            // Only at zero. Deleting one of five changes nothing the heading
            // says, and retitling anyway would fade the same two words out and
            // back for no reason.
            if (_sponsors.Count > 0) return;

            Retitle(ProjectTitle.First, ProjectTitle.Second);

            Shelf.IsHitTestVisible = false;
            Fade(Shelf, to: 0, seconds: ClearSeconds);
            After(ClearSeconds, () => Shelf.Visibility = Visibility.Collapsed);
            Lower();
        };

        return new ContextMenu { Items = { delete } };
    }

    /// <summary>
    /// One sponsor: the artwork above, the name under it.
    ///
    /// The name sits outside the tile rather than inside it, the way the frames
    /// put their sizes under them — the tile is the artwork, and a label inside
    /// it would be something printed on the decal.
    /// </summary>
    private FrameworkElement CardFor(int at)
    {
        var sponsor = _sponsors[at];

        var tile = new Button
        {
            Style = (Style)FindResource("Card"),
            ToolTip = $"Edit {sponsor.Label}",
        };

        tile.Click += (_, _) => Reopen(at);
        tile.ContextMenu = CardMenu(at);

        if (sponsor.Face is { } face && Thumbnail(face) is { } picture)
            tile.Content = new Image { Source = picture, Stretch = Stretch.Uniform };

        var name = new TextBlock
        {
            Text = sponsor.Label,
            Style = (Style)FindResource("CardName"),
            // The tile's width less its own margin, so a long name wraps inside
            // the column the card occupies instead of pushing the one beside it
            // along.
            MaxWidth = 132,
            MaxHeight = 32,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var card = new StackPanel
        {
            // The gaps between cards live here rather than on the tile: a
            // bottom margin on the tile would push the name away from the thing
            // it names.
            Margin = new Thickness(0, 0, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        card.Children.Add(tile);
        card.Children.Add(name);
        return card;
    }
}
