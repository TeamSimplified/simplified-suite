using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Simplified.Shared;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// An SVG on the way in, with its pieces to hand.
///
/// A logo arrives as somebody else's file: a badge with a background plate
/// behind it, a wordmark in a colour that belongs to another team, a slogan
/// nobody wants on a car. Taking those out meant opening a drawing program
/// first, so this opens the file as the shapes it is made of — click one, drop
/// it or paint it, and Import carries on into the sizing as though the file had
/// always looked like that.
///
/// Nothing is written back to the SVG. What leaves here is a drawing, which is
/// rendered and stored as a PNG exactly as an unedited one is, so the file
/// somebody dragged in is still theirs afterwards.
/// </summary>
public static class SvgEditor
{
    private static readonly Regex Hex = new("^#?[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>The artwork as it was left, or null if the import was dropped.</summary>
    public static Drawing? Show(Window owner, string svgPath)
    {
        var parts = SvgPicture.Parts(SvgPicture.Read(svgPath)).ToList();
        if (parts.Count == 0) return null;

        // ── The artwork, as pickable shapes ───────────────────────────
        //
        // The drawing's own coordinates, so every shape is already where its
        // file puts it and a Viewbox does the fitting. Painted in order, so the
        // one on top is the one drawn last, which is the one a click wants.
        var bounds = SvgPicture.ToDrawing(parts).Bounds;

        var canvas = new Canvas
        {
            Width = bounds.Width,
            Height = bounds.Height,
            // So a click on empty space is a click rather than nothing.
            Background = Brushes.Transparent,
            RenderTransform = new TranslateTransform(-bounds.X, -bounds.Y),
        };

        var shapes = new Dictionary<SvgPart, Path>();
        var selected = new HashSet<SvgPart>();

        // What the colour box is showing, which is what the picker opens on.
        Color? shown = null;

        // Not called Shape: that is the type the marks below take their
        // Fill property from, and a local of the same name hides it.
        Path Draw(SvgPart part) => new()
        {
            Data = part.Geometry,
            // Transparent rather than nothing where a part has no fill: it draws
            // the same and it can be clicked inside, which is what somebody
            // expects of a shape they can see the outline of.
            Fill = part.Fill ?? Brushes.Transparent,
            Stroke = part.Stroke?.Brush,
            StrokeThickness = part.Stroke?.Thickness ?? 0,
            Opacity = part.Opacity,
            Clip = part.Clip,
            Cursor = Cursors.Hand,
        };

        foreach (var part in parts)
        {
            var path = Draw(part);
            shapes[part] = path;
            canvas.Children.Add(path);
        }

        // The marks sit above the artwork rather than on it: tinting a shape to
        // show it is picked would hide the colour somebody is deciding about.
        var hoverMark = new Path { IsHitTestVisible = false, Opacity = 0 };
        var pickedMark = new Path { IsHitTestVisible = false, Opacity = 0 };
        hoverMark.SetResourceReference(Shape.FillProperty, "Accent");
        pickedMark.SetResourceReference(Shape.FillProperty, "Accent");
        canvas.Children.Add(hoverMark);
        canvas.Children.Add(pickedMark);

        // ── The state of things ──────────────────────────────────────
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var remove = new Button { Content = "Delete", Width = 80, IsEnabled = false };

        // One box, showing the colour of whatever is picked — the same control
        // the Grid Editor asks a team colour with, down to the template, so a
        // colour box behaves the same wherever somebody meets one in the suite.
        var colourBox = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Template = Simplified.Shared.FlatButton.Template(cornerRadius: 3),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            IsEnabled = false,
            ToolTip = "Click to change the colour of what is picked",
        };
        colourBox.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");

        void Mark()
        {
            pickedMark.Data = selected.Count == 0
                ? null
                : new GeometryGroup
                {
                    FillRule = FillRule.Nonzero,
                    Children = new GeometryCollection(selected.Select(p => p.Geometry)),
                };

            pickedMark.Opacity = selected.Count == 0 ? 0 : 0.45;

            count.Text = selected.Count switch
            {
                0 => $"{parts.Count} part{(parts.Count == 1 ? "" : "s")}  ·  click one to pick it",
                1 => $"1 of {parts.Count} picked",
                _ => $"{selected.Count} of {parts.Count} picked",
            };

            remove.IsEnabled = selected.Count > 0;

            // The colour of what is picked, when they agree on one. Parts picked
            // together that disagree leave the box blank rather than claiming
            // one of them: pressing it would paint them all the same anyway, and
            // showing the first one's colour would say that had already
            // happened.
            var colours = selected.Select(p => p.Colour).Distinct().ToList();
            shown = colours.Count == 1 ? colours[0] : null;

            colourBox.IsEnabled = selected.Count > 0;
            colourBox.Background = shown is { } one
                ? new SolidColorBrush(one)
                : ThemeBrush("SurfaceSunken");
        }

        void Pick(SvgPart part, bool add)
        {
            if (!add) selected.Clear();

            if (!selected.Add(part) && add) selected.Remove(part);

            Mark();
        }

        foreach (var (part, path) in shapes)
        {
            path.MouseEnter += (_, _) =>
            {
                hoverMark.Data = part.Geometry;
                hoverMark.Opacity = selected.Contains(part) ? 0 : 0.22;
            };

            path.MouseLeave += (_, _) => hoverMark.Opacity = 0;

            path.MouseLeftButtonDown += (_, e) =>
            {
                Pick(part, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                           || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                hoverMark.Opacity = 0;
                e.Handled = true;
            };
        }

        // Put on the ground further down rather than on the canvas: the canvas
        // is only as big as the artwork, and clicking the space around it is
        // just as much a click on nothing.

        // ── Painting ─────────────────────────────────────────────────
        void Paint(Color colour)
        {
            foreach (var part in selected)
            {
                part.Paint(colour);

                var path = shapes[part];
                path.Fill = part.Fill ?? Brushes.Transparent;
                path.Stroke = part.Stroke?.Brush;
            }

            Mark();
        }



        remove.Click += (_, _) =>
        {
            foreach (var part in selected)
            {
                canvas.Children.Remove(shapes[part]);
                shapes.Remove(part);
                parts.Remove(part);
            }

            selected.Clear();
            hoverMark.Opacity = 0;
            Mark();
        };

        Mark();

        // ── The window ───────────────────────────────────────────────
        //
        // A checkerboard behind it, not a colour: a logo is as likely to be
        // white as black, and either one disappears against a ground chosen in
        // advance. The squares say "nothing is here" in a way no single colour
        // can.
        var ground = new Border
        {
            Background = Checkerboard(),
            Padding = new Thickness(18),
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = canvas },
        };
        ground.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
        ground.BorderThickness = new Thickness(1);

        // A click that reached here is a click on no part: the parts mark their
        // own as handled, so nothing they were clicked with gets this far.
        ground.MouseLeftButtonDown += (_, _) =>
        {
            selected.Clear();
            hoverMark.Opacity = 0;
            Mark();
        };

        var import = new Button { Content = "Import", Width = 80, IsDefault = true, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(import);
        actions.Children.Add(cancel);

        var tools = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };

        var editing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        editing.Children.Add(colourBox);
        remove.Margin = new Thickness(10, 0, 0, 0);
        editing.Children.Add(remove);

        DockPanel.SetDock(editing, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        tools.Children.Add(editing);
        tools.Children.Add(actions);

        var body = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(tools, Dock.Bottom);
        DockPanel.SetDock(count, Dock.Bottom);
        count.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(tools);
        body.Children.Add(count);
        body.Children.Add(ground);

        var window = new Window
        {
            Title = $"Edit artwork — {System.IO.Path.GetFileName(svgPath)}",
            Content = body,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 860,
            Height = 700,
            MinWidth = 520,
            MinHeight = 420,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
        };

        DialogChrome.Apply(window);

        // One click, as a colour box takes everywhere else in the suite. The
        // square, the hue strip, the eyedropper and the hex are all in there,
        // which is where somebody looking for any of them expects to find them.
        colourBox.Click += (_, _) =>
        {
            if (selected.Count == 0) return;

            var from = shown ?? Colors.White;

            var chosen = Simplified.Shared.ColourDialog.Show(
                window, "Part colour", $"#{from.R:X2}{from.G:X2}{from.B:X2}");

            if (chosen is null || !Hex.IsMatch(chosen)) return;

            var text = chosen.TrimStart('#');
            Paint(Color.FromRgb(
                byte.Parse(text[..2], NumberStyles.HexNumber),
                byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber),
                byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber)));
        };

        import.Click += (_, _) =>
        {
            if (parts.Count == 0)
            {
                MessageDialog.Show(window,
                    "Everything has been deleted, so there is no artwork to import.",
                    "Edit artwork", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            window.DialogResult = true;
        };

        return window.ShowDialog() == true ? SvgPicture.ToDrawing(parts) : null;
    }

    /// <summary>A brush from the palette, or grey if the app has no palette.</summary>
    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>
    /// The two-tone squares that mean "transparent", at the size image editors
    /// draw them.
    /// </summary>
    private static DrawingBrush Checkerboard()
    {
        const double square = 9;

        var light = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null,
            new RectangleGeometry(new Rect(0, 0, square * 2, square * 2)));

        var dark = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)), null,
            new GeometryGroup
            {
                Children = new GeometryCollection
                {
                    new RectangleGeometry(new Rect(0, 0, square, square)),
                    new RectangleGeometry(new Rect(square, square, square, square)),
                },
            });

        var tile = new DrawingGroup();
        tile.Children.Add(light);
        tile.Children.Add(dark);

        return new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, square * 2, square * 2),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }
}
