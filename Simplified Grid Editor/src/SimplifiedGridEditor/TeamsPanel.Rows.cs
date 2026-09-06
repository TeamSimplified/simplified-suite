using System.IO;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Teams;
using SimplifiedGridEditor.Model;
using SimplifiedGridEditor.Util;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// The team list, laid out as a table.
///
/// One line per team — badge, colour, the three names, reset — under column
/// headers, so a grid reads as a grid. The alternative, a stack of labelled
/// fields per team, turns eleven teams into a page of scrolling.
///
/// The badge and the colour are buttons into their own pages rather than
/// editors in the row: a team has four colours and up to four logos, which is
/// more than a line can hold.
/// </summary>
public partial class TeamsPanel
{
    /// <summary>Column widths, shared by the header and every row so they line up.</summary>
    private static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
    }

    private void BuildRows()
    {
        TeamList.Children.Clear();
        _rowVisuals.Clear();
        foreach (var row in _rows) TeamList.Children.Add(BuildRow(row));
        QueueBadgeLoads();
    }

    private UIElement BuildRow(TeamRow row)
    {
        var grid = new Grid();
        DefineColumns(grid);

        // ── Badge. Shows the team's own timing-tower logo, which is the one
        //    small enough to read at this size.
        var badge = new Button
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Edit this team's logos",
            Template = FlatButtonTemplate(),
        };
        // A dark ground of its own, and a border to bound it. These logos are
        // drawn light for a dark game UI, so several of them - Aston Martin,
        // Alpine, Williams - are white artwork that vanishes against a light
        // background. Letting the app show through was right while this app was
        // dark and only dark; it isn't now.
        //
        // PreviewBackdropDark rather than a palette key, for the reason the
        // shared theme gives: this is a judging surface, not chrome, so it has
        // to stay put whichever palette the app is wearing.
        badge.SetResourceReference(Control.BackgroundProperty, "PreviewBackdropDark");
        badge.SetResourceReference(Control.BorderBrushProperty, "BorderSoft");
        badge.BorderThickness = new Thickness(1);

        // Inset well inside the square: a logo that fills its ground edge to
        // edge reads as a tile rather than as a badge. The size comes from the
        // picture itself once it arrives - see SizeToPixels.
        var badgeImage = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(BadgeInset),
        };
        badge.Content = badgeImage;
        badge.Click += (_, _) => ShowLogoPicker(row);
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // ── Colour. One swatch, the team's accent, opening the four.
        var accent = row.Colours.GetValueOrDefault(ColourField.UiTeamColour) ?? row.Team.AccentHex;
        // A thin vertical bar rather than a square, as the launcher has it: it
        // reads as a stripe of the team's colour beside the row instead of
        // competing with the logo next to it.
        var swatch = new Button
        {
            Width = 14,
            Height = 26,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = row.DatabaseIndex is null
                ? "This team has no colours in the database"
                : "Edit this team's colours",
            IsEnabled = row.DatabaseIndex is not null,
            Cursor = row.DatabaseIndex is not null
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow,
            Template = FlatButtonTemplate(cornerRadius: 3),
            Background = Brush(accent),
            BorderThickness = new Thickness(row.ColoursModified ? 2 : 1),
        };
        swatch.SetResourceReference(Control.BorderBrushProperty, row.ColoursModified ? "Accent" : "BorderStrong");
        swatch.Click += (_, _) => ShowColourPicker(row);
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(swatch);

        // ── The three names, as cells of one table.
        AddNameCell(grid, row, 2, () => row.Full, v => row.Full = v, divider: false);
        AddNameCell(grid, row, 3, () => row.Short, v => row.Short = v, divider: true);
        AddNameCell(grid, row, 4, () => row.SuperShort, v => row.SuperShort = v, divider: true);

        // ── Reset.
        // Tag, not Content: IconButton's template has no ContentPresenter and
        // draws its own glyph from Tag, coloured by Foreground. A Path handed
        // in as Content is silently never rendered.
        var reset = new Button
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Put this team back as the game has it",
            Style = FindResource("IconButton") as Style,
            Tag = FindResource("IconReset"),
        }.Themed(Control.ForegroundProperty, row.IsModified ? "Accent" : "TextDisabled");
        reset.Click += (_, _) =>
        {
            row.Reset();
            BuildRows();
            UpdateModifiedCount();
        };
        Grid.SetColumn(reset, 5);
        grid.Children.Add(reset);

        // The row itself. No border of its own: the Panel style's is turned off
        // rather than painted transparent, so nothing shifts as rows change.
        // A changed row is already told by its reset button lighting up and by
        // the count in the status bar.
        var container = new Border
        {
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 4),
            BorderThickness = new Thickness(0),
            Child = grid,
        };
        container.SetResourceReference(StyleProperty, "Panel");

        _rowVisuals[row] = new RowVisuals(badgeImage, reset);
        return container;
    }

    /// <summary>
    /// One editable name.
    ///
    /// Values carry the game's inline formatting — McLaren's short name is
    /// really <c>{o:upper}M{/o}{o:lower}c{/o}…</c> — which is unreadable to edit
    /// around. The cell shows the name without it and swaps to the real text on
    /// focus, so the markup is there to keep or change but not in the way.
    /// Nothing is written back unless the cell is actually edited, so a team
    /// left alone keeps its formatting exactly.
    /// </summary>
    private void AddNameCell(Grid grid, TeamRow row, int column,
        Func<string> get, Action<string> set, bool divider)
    {
        var raw = get();
        var box = new TextBox
        {
            Text = TeamNameEditing.StripFormatting(raw),
            Style = FindResource("CellInput") as Style,
        };
        var showingClean = TeamNameEditing.StripFormatting(raw) != raw;

        box.GotFocus += (_, _) =>
        {
            if (!showingClean) return;
            showingClean = false;
            box.Text = get();
            box.CaretIndex = box.Text.Length;
        };

        box.LostFocus += (_, _) =>
        {
            set(box.Text);
            var current = get();
            var clean = TeamNameEditing.StripFormatting(current);
            showingClean = clean != current;
            if (showingClean) box.Text = clean;
            RefreshRow(row);
        };

        box.TextChanged += (_, _) =>
        {
            if (box.IsFocused) set(box.Text);
            RefreshRow(row);
        };

        var host = new Border { Child = box, BorderThickness = new Thickness(divider ? 1 : 0, 0, 0, 0) };
        host.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
        Grid.SetColumn(host, column);
        grid.Children.Add(host);
    }

    // ─────────────────────────── Row state ────────────────────────────

    private sealed record RowVisuals(Image Badge, Button ResetButton);

    private readonly Dictionary<TeamRow, RowVisuals> _rowVisuals = new();

    /// <summary>
    /// Update just the marks that depend on whether a row differs, rather than
    /// rebuilding it — a rebuild while typing would take the caret with it.
    /// </summary>
    private void RefreshRow(TeamRow row)
    {
        if (_rowVisuals.TryGetValue(row, out var visuals))
        {
            visuals.ResetButton.Themed(Control.ForegroundProperty,
                row.IsModified ? "Accent" : "TextDisabled");
        }
        UpdateModifiedCount();
    }

    // ───────────────────────────── Badges ─────────────────────────────

    /// <summary>The 40pt badge button, its 1pt border, and the inset each side.</summary>
    private const double BadgeInset = 6;
    private const double BadgeRoom = 40 - 2 - 2 * BadgeInset;

    /// <summary>
    /// Fill in the badges after the list is on screen.
    ///
    /// Each one means reading a texture array and running a slice through
    /// texconv, which is far too slow to do while laying out — a dozen teams
    /// would stall the window for seconds. They arrive one at a time instead,
    /// at a priority that lets the list draw and stay responsive first.
    /// </summary>
    private void QueueBadgeLoads()
    {
        foreach (var (row, visuals) in _rowVisuals.ToList())
        {
            var kind = LogoSlices.KindsFor(_season, row.Team.Id).FirstOrDefault(k => k.Id == "badges")
                    ?? LogoSlices.KindsFor(_season, row.Team.Id).FirstOrDefault();
            if (kind is null) continue;

            var slice = LogoSlices.SliceFor(kind, _season, row.Team.Id);
            if (slice is null) continue;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var chosen = row.Logos.GetValueOrDefault(kind.Id);
                var path = chosen ?? Previews.PathFor(kind, slice.Value);
                var image = LoadImage(path);
                visuals.Badge.Source = image;
                SizeToPixels(visuals.Badge, image, BadgeRoom, BadgeRoom);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Draw a bitmap one image pixel to one unit, shrinking it uniformly if it
    /// will not fit in the room given.
    ///
    /// WPF measures a bitmap from its DPI rather than its pixel count, and
    /// texconv stamps these PNGs at 72 — so left to itself every logo here is
    /// drawn a third larger than it is. That is what made a 24-pixel badge look
    /// soft, and, once a cap caught the inflated sizes, what made two logos of
    /// different sizes come out identical on the logos page.
    /// </summary>
    private static void SizeToPixels(Image element, BitmapSource? image, double maxWidth, double maxHeight)
    {
        if (image is null)
        {
            element.ClearValue(FrameworkElement.WidthProperty);
            element.ClearValue(FrameworkElement.HeightProperty);
            return;
        }

        var scale = Math.Min(1.0, Math.Min(
            maxWidth / image.PixelWidth, maxHeight / image.PixelHeight));
        element.Width = image.PixelWidth * scale;
        element.Height = image.PixelHeight * scale;
    }

    private static BitmapImage? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── Helpers ────────────────────────────

    private static SolidColorBrush Brush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// A button that is only its own background — used where the content is the
    /// point, as with a colour swatch or a logo, and the themed button chrome
    /// would sit on top of it.
    /// </summary>
    private static ControlTemplate FlatButtonTemplate(double cornerRadius = 4)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.ClipToBoundsProperty, true);
        foreach (var property in new DependencyProperty[]
                 { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })
            border.SetBinding(property, new System.Windows.Data.Binding(property.Name)
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        // The background is the point of these buttons - a team colour, or a
        // logo - so hover brightens the outline rather than the fill, which
        // would misreport the colour underneath the pointer.
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("TextMuted"), "Chrome"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("TextPrimary"), "Chrome"));
        template.Triggers.Add(pressed);

        return template;
    }
}
