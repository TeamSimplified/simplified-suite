using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimplifiedSponsorEditor.Core.Sponsors;

namespace SimplifiedSponsorEditor;

/// <summary>
/// One row of the decal list.
///
/// The sponsor's totals are stamped on every row rather than worked out by the
/// group header, which can only see the rows a filter left showing — a search
/// for one decal would otherwise report its sponsor as having one.
/// </summary>
public sealed class DecalRow(SponsorDecal decal, bool isReplaced, int sponsorTotal, int sponsorReplaced)
{
    public SponsorDecal Decal { get; } = decal;

    public string Name => Decal.Name;
    public string Sponsor => Decal.Sponsor;
    public bool IsReplaced { get; } = isReplaced;

    /// <summary>e.g. <c>6 decals</c>, or <c>6 decals, 2 replaced</c>.</summary>
    public string SponsorSummary { get; } = sponsorReplaced > 0
        ? $"{sponsorTotal} decal{(sponsorTotal == 1 ? "" : "s")}, {sponsorReplaced} replaced"
        : $"{sponsorTotal} decal{(sponsorTotal == 1 ? "" : "s")}";
}

/// <summary>
/// The decal list — one list, grouped by sponsor — and the picture of whichever
/// row is selected.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the selection is.</summary>
    private SponsorDecal? _selected;

    /// <summary>True while the list is being rebuilt, so its own selection events are ignored.</summary>
    private bool _rebuilding;

    // ── Building the list ─────────────────────────────────────────────

    private void RebuildViews()
    {
        _rebuilding = true;
        try
        {
            // Nothing to edit without a project, so nothing to show. A populated
            // list with every control that acts on it disabled invites clicking
            // things and reads as the app being broken rather than as waiting.
            SearchBox.IsEnabled = _project is not null;
            ReplacedOnlyCheck.IsEnabled = _project is not null;

            if (_project is null)
            {
                DecalList.ItemsSource = null;
                CountLabel.Text = "";
                _selected = null;
                return;
            }

            var replaced = _project.ReplacedDecals.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Totals per sponsor, taken before filtering so a header keeps
            // saying what the sponsor has rather than what is on screen.
            var totals = _catalogue.Decals
                .GroupBy(d => d.Sponsor)
                .ToDictionary(
                    g => g.Key,
                    g => (Total: g.Count(), Replaced: g.Count(d => replaced.Contains(d.Name))));

            var rows = _catalogue.Decals
                .Select(d => new DecalRow(d, replaced.Contains(d.Name),
                                          totals[d.Sponsor].Total, totals[d.Sponsor].Replaced))
                .ToList();

            var view = new ListCollectionView(rows)
            {
                Filter = o => o is DecalRow row && Matches(row),
            };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DecalRow.Sponsor)));

            // Sponsors in name order, and the decals within each in theirs.
            view.SortDescriptions.Add(new SortDescription(nameof(DecalRow.Sponsor), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(DecalRow.Name), ListSortDirection.Ascending));

            DecalList.ItemsSource = view;

            // Studio's form: the total on its own, and shown/total when a filter
            // has narrowed it.
            var shown = view.Cast<object>().Count();
            CountLabel.Text = shown == _catalogue.Decals.Count
                ? $"{shown:N0}"
                : $"{shown:N0}/{_catalogue.Decals.Count:N0}";

            if (replaced.Count > 0) CountLabel.Text += $"   ·   {replaced.Count:N0} replaced";

            // Put the selection back, since the rows are new objects.
            if (_selected is not null)
            {
                DecalList.SelectedItem = view.Cast<DecalRow>()
                    .FirstOrDefault(r => r.Name == _selected.Name);
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// Put the selection somewhere useful once a project is open, rather than
    /// leaving the pane empty until something is clicked.
    ///
    /// A project's replaced decals first, since opening one is usually to see
    /// what it changes; the top of the list otherwise, which is where a new
    /// project starts from.
    /// </summary>
    private void SelectFirst()
    {
        if (DecalList.ItemsSource is not ListCollectionView view) return;

        var rows = view.Cast<DecalRow>().ToList();
        var first = rows.FirstOrDefault(r => r.IsReplaced) ?? rows.FirstOrDefault();
        if (first is null) return;

        DecalList.SelectedItem = first;
        DecalList.ScrollIntoView(first);

        _selected = first.Decal;
        ShowSelectedDecal();
    }

    private bool Matches(DecalRow row)
    {
        if (ReplacedOnlyCheck.IsChecked == true && !row.IsReplaced) return false;

        var filter = SearchBox.Text.Trim();
        if (filter.Length == 0) return true;

        return row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Sponsor.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // ── Selection ─────────────────────────────────────────────────────

    private void Search_TextChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildViews();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        if (DecalList.SelectedItem is DecalRow row)
        {
            _selected = row.Decal;
            ShowSelectedDecal();
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DecalList.SelectedItem is DecalRow) Import_Click(sender, e);
    }

    // ── The picture ───────────────────────────────────────────────────

    private void ShowSelectedDecal()
    {
        var decal = _selected;
        var open = _project is not null;

        ImportButton.IsEnabled = open && decal is not null;
        RevertButton.IsEnabled = open && decal is not null && _project!.IsReplaced(decal.Name);

        if (decal is null)
        {
            ShowPlaceholder(open ? "Select a decal" : "Start a new project, or open one");
            DecalInfoLabel.Text = "";
            ReplacedLabel.Text = "";
            return;
        }

        var replaced = _project?.IsReplaced(decal.Name) ?? false;

        // Deliberately no texture format. What the game stores this as is the
        // packager's business — somebody choosing a picture is handing over a
        // PNG, and BC2_SRGB is not a fact they can act on.
        DecalInfoLabel.Text = decal.HasMenuThumbnail
            ? $"{decal.Name}   ·   {decal.Sponsor}   ·   on the car and in the menu"
            : $"{decal.Name}   ·   {decal.Sponsor}   ·   on the car only";

        ReplacedLabel.Text = replaced ? "● Replaced in this project" : "";

        // The project's artwork when there is some, the game's own otherwise —
        // so the pane always shows what this decal would look like in game.
        var path = replaced ? _project!.PicturePath(decal.Name) : _preview?.Get(decal.CarIdentifier)?.PngPath;

        if (path is null || !File.Exists(path))
        {
            ShowPlaceholder("No picture for this one");
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            // OnLoad, or the file stays locked and replacing it again fails.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();

            PreviewImage.Source = image;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            ApplyBackdrop();
        }
        catch
        {
            ShowPlaceholder("That picture couldn't be read");
        }
    }

    /// <summary>
    /// The empty pane: a line of text on the themed surface, with the backdrop
    /// bar put away. There is nothing to judge, and Studio collapses its bars
    /// in the same state.
    /// </summary>
    private void ShowPlaceholder(string text)
    {
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = text;
        ApplyBackdrop();
    }

    // ── The backdrop ──────────────────────────────────────────────────
    //
    // Studio's, unchanged: the same slider, the same two ends, the same brush
    // held and recoloured in place, and the same rule that the judging colour
    // only applies behind an actual picture.

    // Where the backdrop sits between the two ends: 0 is PreviewBackdropLight,
    // 1 is PreviewBackdropDark. Continuous rather than a pair of presets, since
    // the ground that shows a logo honestly is often neither extreme. Sticky
    // across selections, so several decals can be judged on one ground.
    private double _previewBackdropMix;

    // One brush, recoloured in place. A fresh SolidColorBrush per slider tick
    // would allocate on every pixel of the drag.
    private readonly SolidColorBrush _previewBackdropBrush = new(Colors.White);

    /// <summary>
    /// Point the backdrop slider at the end that matches the app's own theme,
    /// as Studio does. Only the starting position — the slider is the user's
    /// from then on.
    /// </summary>
    private void ApplyThemeToBackdrop() =>
        BackdropSlider.Value = SimplifiedUi.ThemeManager.IsDark ? 1 : 0;

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires from InitializeComponent, before the preview exists.
        if (PreviewSurface is null) return;
        _previewBackdropMix = e.NewValue;
        ApplyBackdrop();
    }

    /// <summary>
    /// Paint the preview's ground. The judging backdrop only applies behind an
    /// actual picture — with nothing up the panel goes back to the themed
    /// surface, so the placeholder text isn't left as grey on near-white.
    /// </summary>
    private void ApplyBackdrop()
    {
        var hasImage = PreviewImage.Source is not null;
        BackdropBar.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        if (hasImage)
        {
            _previewBackdropBrush.Color = BackdropColour(_previewBackdropMix);
            PreviewSurface.Background = _previewBackdropBrush;
        }
        else
        {
            PreviewSurface.Background = ThemeBrush("SurfaceSunken");
        }
    }

    /// <summary>
    /// Mix the two backdrop ends. Straight sRGB interpolation: both ends are
    /// neutral greys, so there is no hue to shift and no third colour to drift
    /// through on the way.
    /// </summary>
    private Color BackdropColour(double mix)
    {
        var light = EndColour("PreviewBackdropLight", Colors.White);
        var dark = EndColour("PreviewBackdropDark", Colors.Black);
        mix = Math.Clamp(mix, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(light.R + (dark.R - light.R) * mix),
            (byte)Math.Round(light.G + (dark.G - light.G) * mix),
            (byte)Math.Round(light.B + (dark.B - light.B) * mix));
    }

    private Color EndColour(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static Brush ThemeBrush(string key, Brush? fallback = null) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback ?? Brushes.Gray;
}
