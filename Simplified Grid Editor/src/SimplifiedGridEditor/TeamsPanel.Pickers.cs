using System.IO;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EgoEngineLibrary.Texconv;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedTeamEditor.Core.Teams;
using SimplifiedGridEditor.Model;
using SimplifiedGridEditor.Util;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// The colour and logo pages.
///
/// Each replaces the list rather than floating over it, so there is one thing
/// on screen at a time and the back arrow is the only way out. Both are built
/// the same way — a row per option, with a big swatch or preview on the left,
/// what it is and where it shows in the middle, and a reset on the right — so
/// moving between them needs no relearning.
/// </summary>
public partial class TeamsPanel
{
    private TeamRow? _pickerRow;

    private void ShowPicker(string title)
    {
        PickerTitle.Text = title;
        ListPage.Visibility = Visibility.Collapsed;
        PickerPage.Visibility = Visibility.Visible;
    }

    private void ClosePicker()
    {
        PickerPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        _pickerRow = null;
    }

    private void PickerBack_Click(object sender, RoutedEventArgs e)
    {
        ClosePicker();

        // The list shows what the pages changed: a swatch, a badge, a mark.
        BuildRows();
        UpdateModifiedCount();
    }

    // ───────────────────────────── Colours ────────────────────────────

    /// <summary>
    /// What each colour actually does, since the names alone don't say. Taken
    /// from the launcher, which worked them out by changing them and looking.
    /// </summary>
    private static string ColourDescription(ColourField field) => field switch
    {
        ColourField.UiTeamColour =>
            "The accent shown next to this team in menus, standings and team-pick screens.",
        ColourField.Primary =>
            "The colour behind the car and driver in the menus.",
        ColourField.Secondary =>
            "A complement to the primary, behind the driver in the menus.",
        ColourField.Tertiary =>
            "Third colour, rarely used if ever.",
        _ => "",
    };

    private void ShowColourPicker(TeamRow row)
    {
        _pickerRow = row;
        ShowPicker($"{TeamNameEditing.StripFormatting(row.Full)} — Colours");
        BuildColourPicker(row);
    }

    private void BuildColourPicker(TeamRow row)
    {
        PickerBody.Children.Clear();

        foreach (var field in TeamColours.Fields)
        {
            if (!row.Colours.TryGetValue(field, out var hex)) continue;

            var original = row.OriginalColours.GetValueOrDefault(field);
            var modified = row.IsColourModified(field);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var swatch = new Button
            {
                Width = 56,
                Height = 56,
                Padding = new Thickness(0),
                Template = Simplified.Shared.FlatButton.Template(),
                Background = Brush(hex),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Click to change",
            };
            swatch.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
            swatch.Click += (_, _) =>
            {
                var chosen = Simplified.Shared.ColourDialog.Show(Host, $"{row.Team.Label} — {TeamColours.FieldLabel(field)}", hex);
                if (chosen is null || chosen == hex) return;
                row.Colours[field] = chosen;
                BuildColourPicker(row);
            };
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var text = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = TeamColours.FieldLabel(field), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = hex,
                FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, modified ? "Accent" : "TextMuted"));
            text.Children.Add(new TextBlock
            {
                Text = ColourDescription(field),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted"));

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (original is not null)
            {
                var reset = ResetButton(modified, $"Put this back to {original}");
                reset.Click += (_, _) =>
                {
                    row.ResetColour(field);
                    BuildColourPicker(row);
                };
                Grid.SetColumn(reset, 2);
                grid.Children.Add(reset);
            }

            PickerBody.Children.Add(PickerCard(grid));
        }
    }

    // ────────────────────────────── Logos ─────────────────────────────

    /// <summary>Where each logo is drawn, which is not obvious from its name.</summary>
    private static string LogoDescription(string kindId) => kindId switch
    {
        "showroom_25" or "showroom_26" => "The large logo on the car showroom and team screens.",
        "badges" => "The small badge on the timing tower and broadcast overlays.",
        "team_select" => "The logo on the career team-select screen.",
        "starting_grid" => "The logo on the starting-grid and results graphics.",
        _ => "",
    };

    private void ShowLogoPicker(TeamRow row)
    {
        _pickerRow = row;
        ShowPicker($"{TeamNameEditing.StripFormatting(row.Full)} — Logos");
        BuildLogoPicker(row);
    }

    private void BuildLogoPicker(TeamRow row)
    {
        PickerBody.Children.Clear();

        var kinds = LogoSlices.KindsFor(_season, row.Team.Id).ToList();
        if (kinds.Count == 0)
        {
            PickerBody.Children.Add(new TextBlock
            {
                Text = $"The game has no logo slots for {row.Team.Label} in "
                     + $"{(_season == Season.Y2026 ? "2026" : "2025")}.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted"));
            return;
        }

        foreach (var kind in kinds)
        {
            var slice = LogoSlices.SliceFor(kind, _season, row.Team.Id)!.Value;
            var chosen = row.Logos.GetValueOrDefault(kind.Id);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // No fixed size: the box takes the logo's own dimensions plus a
            // little padding, the way the launcher does it. These are not all
            // square - the showroom logo is 272 by 156 - and a square box either
            // crops that or floats it in dead space. It also means the box size
            // itself reports the shape of the slot being filled.
            var preview = new Border
            {
                Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(chosen is null ? 1 : 2),
            };
            // A well rather than a second dark grey: SurfaceSunken is the
            // palette's own recessed tone, and it bounds the image on a card.
            preview.SetResourceReference(Border.BackgroundProperty, "SurfaceSunken");
            preview.SetResourceReference(Border.BorderBrushProperty, chosen is null ? "BorderSoft" : "Accent");
            var source = chosen ?? Previews.PathFor(kind, slice);
            var image = LoadImage(source);
            preview.Child = PreviewImage(image);

            // Right-click to keep the picture. What is behind the preview is
            // already a PNG - either the file imported or the one texconv made
            // of the game's slice - so this copies it rather than re-encoding,
            // and what lands on disk is exactly what the slot holds.
            if (source is not null)
                preview.ContextMenu = ExportMenu(source, $"{row.Team.Label} {kind.Label}");
            Grid.SetColumn(preview, 0);
            grid.Children.Add(preview);

            var text = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = kind.Label, FontWeight = FontWeights.SemiBold });
            var size = SizeNote(image, chosen, kind);
            text.Children.Add(new TextBlock
            {
                Text = Describe(chosen, slice)
                     + (size.Length > 0 ? $"  \u00b7  {size}" : ""),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, chosen is null ? "TextMuted" : "Accent"));
            text.Children.Add(new TextBlock
            {
                Text = LogoDescription(kind.Id),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted"));

            // A badge is drawn from more than one array, and saying so explains
            // why one picture lands in three places.
            if (kind.Texarrays.Count > 1)
                text.Children.Add(new TextBlock
                {
                    Text = $"Written into {kind.Texarrays.Count} texture arrays",
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                }.Themed(TextBlock.ForegroundProperty, "TextDisabled"));

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var choose = new Button { Content = "Replace", MinWidth = 88 };
            choose.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"{row.Team.Label} — {kind.Label}",
                    Filter = "PNG image (*.png)|*.png",
                };
                if (dialog.ShowDialog(Host) != true) return;
                row.Logos[kind.Id] = dialog.FileName;
                BuildLogoPicker(row);
            };
            buttons.Children.Add(choose);

            if (chosen is not null)
            {
                var reset = ResetButton(true, "Put the game's own logo back");
                reset.Margin = new Thickness(6, 0, 0, 0);
                reset.Click += (_, _) =>
                {
                    row.Logos.Remove(kind.Id);
                    BuildLogoPicker(row);
                };
                buttons.Children.Add(reset);
            }

            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            PickerBody.Children.Add(PickerCard(grid));
        }
    }

    /// <summary>
    /// The size line under a logo: what the picture is, and — when that is not
    /// the slot's own size — what it is going to become.
    ///
    /// Every slice of a texture array shares one size, so a picture of any other
    /// size is fitted on the way in. Saying so here is what stops somebody
    /// resizing a wide logo to a square themselves to satisfy the slot: the
    /// game's own badges are letterboxed inside theirs, and a wordmark squashed
    /// to 24x24 is what that mistake looks like in game.
    /// </summary>
    private string SizeNote(BitmapImage? image, string? chosen, LogoKind kind)
    {
        if (image is null) return "";

        var mine = $"{image.PixelWidth}\u00d7{image.PixelHeight}";
        if (chosen is null || Previews.SizeFor(kind) is not { } slot) return mine;

        var slotSize = $"{slot.Width}\u00d7{slot.Height}";

        return SliceFit.Plan((uint)image.PixelWidth, (uint)image.PixelHeight, slot.Width, slot.Height) switch
        {
            FitKind.Scaled => $"{mine} \u2192 scaled to {slotSize}",
            FitKind.Padded => $"{mine} \u2192 fitted into {slotSize}",
            _ => mine,
        };
    }

    /// <summary>
    /// What the line under a logo's name says.
    ///
    /// A picture the project already holds is named after its slot rather than
    /// after the file it was picked from — the project renames it when it
    /// stores it — so repeating that name would say "badges-6.png" under a
    /// heading that already reads "Timing tower badge". One just chosen still
    /// has the name it was chosen under, which is worth showing.
    /// </summary>
    private string Describe(string? chosen, int slice)
    {
        if (chosen is null) return $"The game's own, slice {slice}";

        var stored = _project is not null
                     && string.Equals(IOPath.GetDirectoryName(chosen), _project.LogosFolder,
                         StringComparison.OrdinalIgnoreCase);

        return stored ? $"Yours, slice {slice}" : IOPath.GetFileName(chosen);
    }

    /// <summary>
    /// A logo at its own size, so the box around it reports the true shape of
    /// the slot.
    ///
    /// The cap is a backstop for an imported logo far bigger than the slot. It
    /// sits above every size the game itself uses — the largest is the 272×156
    /// showroom logo — so a real slot is never scaled.
    /// </summary>
    private static Image PreviewImage(BitmapSource? image)
    {
        var element = new Image { Stretch = Stretch.Uniform, Source = image };
        SizeToPixels(element, image, maxWidth: 360, maxHeight: 240);
        return element;
    }

    /// <summary>
    /// Saving a logo out, so one can be taken away and worked on.
    ///
    /// The name offered is the team and the slot, which is what the file is
    /// for; anything Windows will not take in a filename is dropped rather
    /// than the save being refused for it.
    /// </summary>
    private ContextMenu ExportMenu(string sourcePath, string suggestedName)
    {
        var export = new MenuItem { Header = "Export as PNG…" };
        export.Click += (_, _) =>
        {
            var invalid = IOPath.GetInvalidFileNameChars();
            var name = new string(suggestedName.Where(c => !invalid.Contains(c)).ToArray()).Trim();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export logo",
                Filter = "PNG image (*.png)|*.png",
                FileName = (name.Length > 0 ? name : "logo") + ".png",
                DefaultExt = ".png",
            };
            if (dialog.ShowDialog(Host) != true) return;

            try
            {
                File.Copy(sourcePath, dialog.FileName, overwrite: true);
                Say($"Exported {IOPath.GetFileName(dialog.FileName)}.");
            }
            catch (Exception ex)
            {
                MessageDialog.Show(Host, $"The image could not be written.\n\n{ex.Message}",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var menu = new ContextMenu();
        menu.Items.Add(export);
        return menu;
    }

    // ───────────────────────────── Shared ─────────────────────────────

    private Border PickerCard(UIElement content)
    {
        var card = new Border
        {
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
        card.SetResourceReference(StyleProperty, "Panel");
        return card;
    }

    /// <summary>
    /// Tag, not Content: IconButton's template has no ContentPresenter and
    /// draws its own glyph from Tag, coloured by Foreground.
    /// </summary>
    private Button ResetButton(bool modified, string tooltip) => new Button
    {
        Width = 32,
        Height = 32,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tooltip,
        Style = FindResource("IconButton") as Style,
        Tag = FindResource("IconReset"),
    }.Themed(Control.ForegroundProperty, modified ? "Accent" : "TextDisabled");
}
