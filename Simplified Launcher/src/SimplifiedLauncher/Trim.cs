using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimplifiedLauncher;

/// <summary>
/// Cuts a <see cref="TextBlock"/>'s text down to the room its column has, and
/// ends what is left in two dots.
///
/// WPF trims text itself and its own trimming is not this: TextTrimming draws a
/// single ellipsis glyph, which is one character rather than two stops and not
/// something that can be swapped. So the cutting is done here.
///
/// Bind <c>Trim.Full</c> where <c>Text</c> would have gone. The whole name is
/// kept here and the block is handed as much of it as fits, so a row that is
/// given more room later gets the rest of its name back — a control that had
/// been handed the short version would have lost it.
///
/// This belongs on the one star column of a Grid, which is handed what is left
/// after the columns that must be shown in full have taken theirs. That is what
/// makes the author and the version always visible and the name the thing that
/// gives way.
/// </summary>
public static class Trim
{
    /// <summary>What the text would say with room for all of it.</summary>
    public static readonly DependencyProperty FullProperty = DependencyProperty.RegisterAttached(
        "Full", typeof(string), typeof(Trim), new PropertyMetadata(null, OnFullChanged));

    public static void SetFull(DependencyObject target, string value) => target.SetValue(FullProperty, value);

    public static string? GetFull(DependencyObject target) => (string?)target.GetValue(FullProperty);

    /// <summary>
    /// Two stops rather than three, and rather than the one-glyph ellipsis:
    /// asked for, and at this size the difference is visible.
    /// </summary>
    private const string Tail = "..";

    private static void OnFullChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock text) return;

        // Rows are reused as the list is rebuilt, so this runs again on a block
        // that is already listening. Dropping the handler first is what stops a
        // row collecting one per mod it has ever shown.
        text.SizeChanged -= Resized;
        text.SizeChanged += Resized;

        Fit(text);
    }

    private static void Resized(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Fit((TextBlock)sender);
    }

    /// <summary>
    /// What is left for this text: the width of the column it sits in, less
    /// whatever else is in there with it.
    ///
    /// Not the block's own ActualWidth, and the difference is the whole of why
    /// the first attempt at this did nothing. A TextBlock that does not wrap
    /// measures to the width of its text whatever it is given, and WPF arranges
    /// an element that wants more than its slot at the size it wants and then
    /// clips it. So a name too long for its row came back with an ActualWidth of
    /// its own text — measure against that and everything fits, always, and
    /// nothing is ever cut.
    ///
    /// The column is asked instead, and it has to be a star column: an Auto one
    /// is sized by what is in it, which puts us back to measuring the name
    /// against itself, and a Grid whose columns want more than it was given
    /// overflows and reports the wider figure as its own width. A star is the
    /// one thing here that is never wider than what it was handed.
    ///
    /// What shares the column — the author, the version — is taken off at the
    /// width each of them asks for, so they are shown in full and the name is
    /// the only one that gives way. None of this depends on the name, so cutting
    /// it cannot change the answer and ask for it to be cut again.
    /// </summary>
    private static double Room(TextBlock text)
    {
        if (Cell(text) is not ({ } grid, { } column, { } inside)) return text.ActualWidth;

        var room = grid.ColumnDefinitions[column].ActualWidth - text.Margin.Left - text.Margin.Right;

        foreach (var sharing in inside.Children.OfType<FrameworkElement>())
            if (!ReferenceEquals(sharing, text))
                room -= sharing.DesiredSize.Width;

        return room;
    }

    /// <summary>
    /// The grid column this text is laid out in, and the panel it shares with.
    ///
    /// Walks up until it finds the Grid, because the text is not a child of one
    /// directly — it sits in a panel that is.
    /// </summary>
    private static (Grid Grid, int Column, Panel Inside)? Cell(TextBlock text)
    {
        if (VisualTreeHelper.GetParent(text) is not Panel inside) return null;
        if (VisualTreeHelper.GetParent(inside) is not Grid grid) return null;

        var column = Grid.GetColumn(inside);
        return column < grid.ColumnDefinitions.Count ? (grid, column, inside) : null;
    }

    /// <summary>
    /// Put as much of the name in as fits, and two dots if it did not all fit.
    ///
    /// Measured with the block's own typeface and size, through the formatting
    /// mode it draws in: at fifteen pixels, Ideal and Display disagree by enough
    /// to cut a letter early or leave one hanging over the edge.
    /// </summary>
    private static void Fit(TextBlock text)
    {
        var full = GetFull(text) ?? "";
        var room = Room(text);

        // Before the first layout the row has no width at all, which is not the
        // same as a row with nothing left in it. The whole name goes in and the
        // pass that follows cuts it.
        if (!Laid(text))
        {
            Show(text, full);
            return;
        }

        if (Width(text, full) <= room)
        {
            Show(text, full);
            return;
        }

        // The longest prefix that still fits with the dots on the end. Binary
        // search rather than a letter at a time: a measurement is not free and
        // this runs for every row of every list.
        var low = 0;
        var high = full.Length;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (Width(text, Cut(full, middle)) <= room) low = middle;
            else high = middle - 1;
        }

        // Nothing fits, not even one letter and the dots. The dots alone say
        // "there is a name here" better than an empty row does.
        Show(text, low == 0 ? Tail : Cut(full, low));
    }

    /// <summary>
    /// Whether the row has been through a layout pass yet. Until it has, the
    /// column has no width and nothing can be fitted to it.
    /// </summary>
    private static bool Laid(TextBlock text) =>
        Cell(text) is ({ } grid, { } column, _) && grid.ColumnDefinitions[column].ActualWidth > 0;

    /// <summary>
    /// The first <paramref name="letters"/> of it, with the dots. Trailing
    /// spaces come off first, so a name cut after a word does not read as
    /// "Season .." with a hole in it.
    /// </summary>
    private static string Cut(string full, int letters) => full[..letters].TrimEnd() + Tail;

    /// <summary>
    /// Only when it has actually changed. Setting Text lays the row out again,
    /// and a row laid out again raises the size change that brought us here.
    /// </summary>
    private static void Show(TextBlock text, string what)
    {
        if (text.Text != what) text.Text = what;
    }

    private static double Width(TextBlock text, string candidate) =>
        new FormattedText(
            candidate,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize,
            Brushes.Black,
            numberSubstitution: null,
            TextOptions.GetTextFormattingMode(text),
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
