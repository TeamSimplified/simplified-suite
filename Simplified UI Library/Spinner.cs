using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SimplifiedUi;

/// <summary>
/// A small turning ring, for the left of a status line.
///
/// Sized to the text beside it rather than to itself: the suite's status bars
/// are 13px type, so the ring is 13px, and the two share a baseline instead of
/// one sitting in the other's whitespace.
///
/// Drawn rather than templated. A ProgressBar in indeterminate mode brings its
/// own chrome from system colours, which arrives as the one light rectangle on
/// a dark palette — the same reason the rest of this library retemplates
/// everything. Two strokes and one animation is less code than fixing that.
/// </summary>
public sealed class Spinner : FrameworkElement
{
    /// <summary>Whether it is turning. False takes it off screen entirely.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(Spinner),
            new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Across, in pixels. The status line's type size.</summary>
    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(Spinner),
            new FrameworkPropertyMetadata(13.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private readonly RotateTransform _turn = new();

    public Spinner()
    {
        RenderTransform = _turn;
        RenderTransformOrigin = new Point(0.5, 0.5);
        // Nothing to see until there is something to wait for, and no room
        // taken either: a collapsed spinner leaves the status text where it
        // sits when the app is idle.
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (Spinner)d;
        var on = (bool)e.NewValue;

        spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            spinner._turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(0.9),
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            // Cleared rather than left at its last angle: an animation still
            // running on a collapsed element is a timer for nothing.
            spinner._turn.BeginAnimation(RotateTransform.AngleProperty, null);
            spinner._turn.Angle = 0;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var d = Diameter;
        if (d <= 0) return;

        // Thin enough to read as a hairline at 13px, never thinner than a
        // pixel and a half or it disappears on a non-integer scale factor.
        var thickness = Math.Max(1.5, d / 7);
        var radius = (d - thickness) / 2;
        var centre = new Point(d / 2, d / 2);

        // The whole ring, faint: without it the arc reads as a comma spinning
        // in space rather than as something going round a track.
        dc.DrawEllipse(null, new Pen(Themed("SurfaceSunken", Brushes.Gainsboro), thickness),
                       centre, radius, radius);

        // Three quarters of it, lit. The gap is what shows the rotation.
        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            draw.BeginFigure(new Point(centre.X, centre.Y - radius), isFilled: false, isClosed: false);
            draw.ArcTo(new Point(centre.X - radius, centre.Y), new Size(radius, radius),
                       rotationAngle: 0, isLargeArc: true, SweepDirection.Clockwise,
                       isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();

        dc.DrawGeometry(null, new Pen(Themed("Accent", Brushes.SteelBlue), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        }, geometry);
    }

    /// <summary>
    /// A palette brush, looked up per render rather than held: these windows
    /// can have their theme swapped underneath them from Settings, and a cached
    /// brush would keep the colour it was built with.
    /// </summary>
    private static Brush Themed(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
