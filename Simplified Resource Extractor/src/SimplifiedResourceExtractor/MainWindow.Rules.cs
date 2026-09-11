using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EgoEngineLibrary.Archive.Erp;
using Primitives = System.Windows.Controls.Primitives;
using Microsoft.Win32;
using SimplifiedResourceExtractor.Core.Patterns;
using SimplifiedResourceExtractor.Core.Scan;

namespace SimplifiedResourceExtractor;

/// <summary>
/// The rule list as a whole: the four things that can be done to it, and the
/// line numbers down its left edge.
/// </summary>
public partial class MainWindow
{
    private const string TextFilter = "Text files|*.txt|All files|*.*";

    // ── Line numbers ──────────────────────────────────────────────────

    /// <summary>
    /// How many numbers the gutter is currently showing, so that typing within
    /// a line costs nothing. A list built from an archive runs to thousands of
    /// lines, and rebuilding that column of digits on every keystroke would be
    /// felt.
    /// </summary>
    private int _gutterLines = -1;

    /// <summary>The editor's horizontal scrollbar, whose height the gutter has to leave room for.</summary>
    private Primitives.ScrollBar? _editorScrollBar;

    /// <summary>
    /// Tie the gutter to the editor's scrolling.
    ///
    /// A TextBox does not raise a scroll event of its own, so the ScrollViewer
    /// inside its template is what gets asked. It only exists once the template
    /// has been applied, which is why this waits for Loaded rather than running
    /// in the constructor.
    /// </summary>
    private void HookGutter()
    {
        RulesBox.ApplyTemplate();

        if (RulesBox.Template.FindName("PART_ContentHost", RulesBox) is ScrollViewer host)
        {
            host.ScrollChanged += (_, e) =>
            {
                RulesGutter.ScrollToVerticalOffset(e.VerticalOffset);
                ReserveScrollBarRow(host);
            };

            ReserveScrollBarRow(host);
        }

        UpdateGutter();
    }

    /// <summary>
    /// Keep the bottom of the numbers level with the bottom of the text.
    ///
    /// The editor's horizontal scrollbar takes a row's worth of height out of
    /// its own viewport. The gutter has no scrollbar, so without this it is
    /// taller by exactly that much and shows one extra number, sitting beside
    /// the scrollbar rather than beside a line of text.
    ///
    /// The bar is asked for its own height rather than inferred from the
    /// viewport — that difference reads as zero or as the whole control while
    /// layout is still settling, and the gutter was left a line short with no
    /// later event to correct it. SystemParameters is no good either: the suite
    /// retemplates its scrollbars and they are not the system's size.
    /// </summary>
    private void ReserveScrollBarRow(ScrollViewer host)
    {
        host.ApplyTemplate();
        _editorScrollBar ??= host.Template?.FindName("PART_HorizontalScrollBar", host) as Primitives.ScrollBar;

        var reserved = _editorScrollBar is { Visibility: Visibility.Visible }
            ? _editorScrollBar.ActualHeight
            : 0;

        // Only when it has actually changed: assigning Margin forces a layout
        // pass, which raises ScrollChanged again.
        if (Math.Abs(RulesGutter.Margin.Bottom - reserved) > 0.5)
        {
            RulesGutter.Margin = new Thickness(0, 0, 0, reserved);
        }
    }

    /// <summary>
    /// Renumber, when the number of lines has actually changed.
    /// </summary>
    private void UpdateGutter()
    {
        // Counted from the text rather than read off TextBox.LineCount, which
        // answers -1 until the control has been measured and counts wrapped
        // rows rather than lines once it has. Wrapping is off here, but the
        // first of those would leave the gutter empty on the way up.
        var lines = 1;
        foreach (var c in RulesBox.Text)
        {
            if (c == '\n') lines++;
        }

        if (lines == _gutterLines) return;
        _gutterLines = lines;

        var numbers = new StringBuilder(lines * 5);
        for (var i = 1; i <= lines; i++)
        {
            if (i > 1) numbers.Append('\n');
            numbers.Append(i);
        }

        RulesGutter.Text = numbers.ToString();

        // Wide enough for the highest number, asked of the control rather than
        // worked out beside it. A TextBox does not size to its content, so the
        // width has to be set - but measuring the string separately with
        // FormattedText came out well short of what the TextBox then needed,
        // and the numbers rendered as a clipped sliver. Measuring the TextBox
        // itself against unbounded width uses the same path that draws it.
        RulesGutter.Width = double.NaN;
        RulesGutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RulesGutter.Width = Math.Ceiling(RulesGutter.DesiredSize.Width);
    }

    /// <summary>Back to the worked example the list starts as.</summary>
    private void RulesReset_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmReplacingRules("Reset the rules?", "Reset")) return;

        RulesBox.Text = ReferenceList.DefaultText;
        SaveRules();
        Status("Rules reset.");
    }

    /// <summary>
    /// Add everything an archive holds to the list, one identifier per line.
    ///
    /// Read with <see cref="ErpIndex"/>, so naming a 195 MB archive costs the
    /// resource table and nothing else — none of the payloads are touched to
    /// answer "what is in here".
    /// </summary>
    private void RulesFromErp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "List the resources in an archive",
            Filter = "ERP archives|*" + ArchiveScanner.ErpExtension + "|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = StartingFolder(SearchBox.Text),
        };

        if (picker.ShowDialog(this) != true) return;

        var index = ErpIndex.TryRead(picker.FileName);
        if (index is null)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(picker.FileName)} couldn't be read as an ERP.\n\n" +
                "It may be a different format, or damaged.",
                "Not an archive", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var identifiers = index.Resources.Select(r => r.Identifier).ToList();
        var already = ReferenceList.CountAlreadyPresent(RulesBox.Text, identifiers);

        RulesBox.Text = ReferenceList.Append(RulesBox.Text, identifiers, Path.GetFileName(picker.FileName));
        RulesBox.ScrollToEnd();
        SaveRules();

        Status(already > 0
            ? $"Added {identifiers.Count - already:N0} from {Path.GetFileName(picker.FileName)} — {already:N0} were already listed."
            : $"Added {identifiers.Count:N0} from {Path.GetFileName(picker.FileName)}.");
    }

    /// <summary>Save the list somewhere it can be kept or passed on.</summary>
    private void RulesExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "Export the rules",
            Filter = TextFilter,
            DefaultExt = ".txt",
            FileName = "reference.txt",
            InitialDirectory = StartingFolder(OutputBox.Text),
        };

        if (picker.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(picker.FileName, RulesBox.Text, Encoding.UTF8);
            Status($"Exported to {picker.FileName}");
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"The rules couldn't be written.\n\n{ex.Message}",
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Take a list from somewhere else, in place of this one.</summary>
    private void RulesImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Import rules",
            Filter = TextFilter,
            CheckFileExists = true,
            InitialDirectory = StartingFolder(OutputBox.Text),
        };

        if (picker.ShowDialog(this) != true) return;
        if (!ConfirmReplacingRules($"Replace the rules with {Path.GetFileName(picker.FileName)}?", "Replace")) return;

        try
        {
            RulesBox.Text = File.ReadAllText(picker.FileName);
            SaveRules();
            Status($"Imported {Path.GetFileName(picker.FileName)}.");
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this,
                $"{Path.GetFileName(picker.FileName)} couldn't be read.\n\n{ex.Message}",
                "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Ask before throwing the list away — but only when there is something in
    /// it to throw. A list of nothing but the example comments is not work
    /// anybody minds losing, and a dialog for it is just a step in the way.
    /// </summary>
    private bool ConfirmReplacingRules(string question, string confirmLabel)
    {
        if (ReferenceList.Parse(RulesBox.Text).RuleCount == 0) return true;

        // Cancel last, so Esc and the title-bar close both land on the choice
        // that changes nothing.
        return SimplifiedUi.MessageDialog.Choose(this, "Rules", question,
            "What's in the box now will be lost. Export it first if you want to keep it.",
            (confirmLabel, MessageBoxResult.Yes),
            ("Cancel", MessageBoxResult.Cancel)) == MessageBoxResult.Yes;
    }
}
