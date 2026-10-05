using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SimplifiedUi;

namespace Simplified.Shared;

/// <summary>
/// Asking for one colour.
///
/// Shared rather than copied: the Grid Editor and the Team Editor each had one
/// of these, identical but for the namespace, and the Sponsor Editor wanted a
/// third. One of them getting a fix the others did not is the whole reason the
/// suite keeps its common parts in one place.
///
/// A saturation/value square with a hue strip under it, and the hex beside
/// them. The square is for choosing — a team colour is usually judged by eye —
/// and the hex is for the times a colour arrives as a number, copied off a
/// brand page or out of an image editor. They are one value seen two ways, so
/// editing either moves the other.
///
/// The old colour sits next to the new one and puts itself back when clicked,
/// which is what makes it safe to drag around and look.
/// </summary>
public static class ColourDialog
{
    private static readonly Regex Valid = new("^#?[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private const double SquareWidth = 304;
    private const double SquareHeight = 170;
    private const double StripHeight = 18;

    /// <summary>The chosen colour as <c>#RRGGBB</c>, or null if cancelled.</summary>
    public static string? Show(Window owner, string title, string current)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.Height,
            Width = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };

        var body = new StackPanel { Margin = new Thickness(18) };

        // ── The square. Three layers: the hue flat underneath, white fading
        //    out to the right for saturation, black fading in downwards for
        //    value. The transparent ends are the same colour as their solid
        //    ends with the alpha taken off — a plain Transparent would fade
        //    through grey and dirty the middle of the square.
        var hueLayer = new Border { Background = Brushes.Red };
        var whiteLayer = new Border
        {
            Background = new LinearGradientBrush(
                Colors.White, Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0.5), new Point(1, 0.5)),
        };
        var blackLayer = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(0, 0, 0, 0), Colors.Black,
                new Point(0.5, 0), new Point(0.5, 1)),
        };

        // A white ring on a dark halo, so it stays visible on both a pale
        // corner and a black one.
        var ringHalo = new Ellipse
        {
            Width = 16, Height = 16, StrokeThickness = 3,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
        };
        var ring = new Ellipse
        {
            Width = 16, Height = 16, StrokeThickness = 2, Stroke = Brushes.White,
        };
        var squareCanvas = new Canvas { IsHitTestVisible = false };
        squareCanvas.Children.Add(ringHalo);
        squareCanvas.Children.Add(ring);

        var square = new Grid
        {
            Width = SquareWidth,
            Height = SquareHeight,
            Cursor = Cursors.Cross,
            // The size is fixed, so the rounded clip can be an exact rectangle
            // rather than something recomputed on every layout pass.
            Clip = new RectangleGeometry(new Rect(0, 0, SquareWidth, SquareHeight), 4, 4),
        };
        square.Children.Add(hueLayer);
        square.Children.Add(whiteLayer);
        square.Children.Add(blackLayer);
        square.Children.Add(squareCanvas);
        body.Children.Add(square);

        // ── The hue strip, a full turn of the wheel from red back to red.
        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i <= 6; i++)
            hueBrush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));

        var hueMarker = new Rectangle
        {
            Width = 7, Height = StripHeight, RadiusX = 3, RadiusY = 3,
            Stroke = Brushes.White, StrokeThickness = 2,
        };
        var hueCanvas = new Canvas { IsHitTestVisible = false };
        hueCanvas.Children.Add(hueMarker);

        var strip = new Grid
        {
            Width = SquareWidth,
            Height = StripHeight,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Cross,
            Clip = new RectangleGeometry(new Rect(0, 0, SquareWidth, StripHeight), 4, 4),
        };
        strip.Children.Add(new Border { Background = hueBrush });
        strip.Children.Add(hueCanvas);
        body.Children.Add(strip);

        // ── Hex, the colour now, and the colour before.
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            Text = current,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
        };
        Grid.SetColumn(box, 0);
        row.Children.Add(box);

        var dropper = new Button
        {
            Width = 30,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Take a colour off the screen",
            Style = Application.Current.FindResource("IconButton") as Style,
            Tag = Application.Current.FindResource("IconEyedropper"),
        };
        Grid.SetColumn(dropper, 1);
        row.Children.Add(dropper);

        var now = Swatch("The colour now");
        now.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(now, 2);
        row.Children.Add(now);

        var before = Swatch("What it was — click to put it back");
        before.Margin = new Thickness(6, 0, 0, 0);
        before.Cursor = Cursors.Hand;
        before.Background = Parse(current) is { } start
            ? new SolidColorBrush(start)
            : Brushes.Transparent;
        Grid.SetColumn(before, 3);
        row.Children.Add(before);
        body.Children.Add(row);

        var hint = new TextBlock
        {
            Text = "Six hex digits, with or without the #.",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button { Content = "Set", MinWidth = 84, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        // ── The one value behind both halves of the dialog.
        //
        //    Held as hue/saturation/value rather than as the hex, because grey
        //    and black have no hue of their own to read back: dragging to the
        //    bottom of the square and out again would otherwise come back red
        //    every time instead of the colour you left.
        var (h, s, v) = ToHsv(Parse(current) ?? Color.FromRgb(0x80, 0x80, 0x80));
        var syncing = false;

        void Draw()
        {
            var colour = FromHsv(h, s, v);
            hueLayer.Background = new SolidColorBrush(FromHsv(h, 1, 1));
            now.Background = new SolidColorBrush(colour);

            Canvas.SetLeft(ring, s * SquareWidth - ring.Width / 2);
            Canvas.SetTop(ring, (1 - v) * SquareHeight - ring.Height / 2);
            Canvas.SetLeft(ringHalo, Canvas.GetLeft(ring));
            Canvas.SetTop(ringHalo, Canvas.GetTop(ring));
            Canvas.SetLeft(hueMarker, h / 360 * SquareWidth - hueMarker.Width / 2);

            syncing = true;
            box.Text = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
            syncing = false;

            ok.IsEnabled = true;
            hint.Visibility = Visibility.Collapsed;
        }

        box.TextChanged += (_, _) =>
        {
            if (syncing) return;

            var typed = Parse(box.Text);
            if (typed is null)
            {
                ok.IsEnabled = false;
                hint.Visibility = Visibility.Visible;
                return;
            }

            var (th, ts, tv) = ToHsv(typed.Value);
            // Keep the hue a grey cannot state, and the saturation a black
            // cannot, so typing down to 000000 and back up is not a reset.
            h = ts > 0 ? th : h;
            s = tv > 0 ? ts : s;
            v = tv;
            Draw();
        };

        void PickSquare(Point p)
        {
            s = Math.Clamp(p.X / SquareWidth, 0, 1);
            v = 1 - Math.Clamp(p.Y / SquareHeight, 0, 1);
            Draw();
        }

        void PickHue(Point p)
        {
            h = Math.Clamp(p.X / SquareWidth, 0, 1) * 360;
            Draw();
        }

        // Capture on press: a drag that leaves the square keeps picking off its
        // edge rather than stopping dead where the pointer crossed the line.
        square.MouseLeftButtonDown += (_, e) => { square.CaptureMouse(); PickSquare(e.GetPosition(square)); };
        square.MouseMove += (_, e) => { if (square.IsMouseCaptured) PickSquare(e.GetPosition(square)); };
        square.MouseLeftButtonUp += (_, _) => square.ReleaseMouseCapture();

        strip.MouseLeftButtonDown += (_, e) => { strip.CaptureMouse(); PickHue(e.GetPosition(strip)); };
        strip.MouseMove += (_, e) => { if (strip.IsMouseCaptured) PickHue(e.GetPosition(strip)); };
        strip.MouseLeftButtonUp += (_, _) => strip.ReleaseMouseCapture();

        before.MouseLeftButtonDown += (_, _) =>
        {
            if (Parse(current) is not { } original) return;
            (h, s, v) = ToHsv(original);
            Draw();
        };

        dropper.Click += (_, _) =>
        {
            if (ScreenColourPicker.Pick(window) is not { } sampled) return;
            var (sh, ss, sv) = ToHsv(sampled);
            // Same care as the hex box: a grey off the screen keeps the hue it
            // was pointed at rather than snapping the square back to red.
            h = ss > 0 ? sh : h;
            s = sv > 0 ? ss : s;
            v = sv;
            Draw();
        };

        string? chosen = null;
        ok.Click += (_, _) =>
        {
            chosen = box.Text.Trim().StartsWith('#')
                ? box.Text.Trim().ToUpperInvariant()
                : "#" + box.Text.Trim().ToUpperInvariant();
            window.DialogResult = true;
        };

        window.Content = body;
        // The window's own Title is what the chrome shows.
        SimplifiedUi.DialogChrome.Apply(window);
        Draw();

        window.Loaded += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };

        return window.ShowDialog() == true ? chosen : null;
    }

    private static Border Swatch(string tooltip) => new Border
    {
        Width = 38,
        Height = 30,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tooltip,
    }.Themed(Border.BorderBrushProperty, "BorderStrong");

    private static Color? Parse(string? text)
    {
        text = text?.Trim();
        if (text is null || !Valid.IsMatch(text)) return null;
        var digits = text.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(digits[..2], 16),
            Convert.ToByte(digits[2..4], 16),
            Convert.ToByte(digits[4..], 16));
    }

    // ─────────────────────── Hue, saturation, value ───────────────────────

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var span = max - min;

        double h = 0;
        if (span > 0)
        {
            if (max == r) h = (g - b) / span + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / span + 2;
            else h = (r - g) / span + 4;
            h *= 60;
        }

        return (h, max <= 0 ? 0 : span / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var chroma = v * s;
        var second = chroma * (1 - Math.Abs(h / 60 % 2 - 1));
        var floor = v - chroma;

        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (chroma, second, 0.0),
            1 => (second, chroma, 0.0),
            2 => (0.0, chroma, second),
            3 => (0.0, second, chroma),
            4 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + floor) * 255),
            (byte)Math.Round((g + floor) * 255),
            (byte)Math.Round((b + floor) * 255));
    }
}
