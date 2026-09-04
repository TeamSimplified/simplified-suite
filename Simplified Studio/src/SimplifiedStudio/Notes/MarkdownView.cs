using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SimplifiedStudio.Core.Notes;

namespace SimplifiedStudio.Notes;

/// <summary>
/// Draws a parsed note, and lets its task lists be ticked.
///
/// A stack of elements rather than a <c>FlowDocument</c>, because the checkbox
/// is the point: a real <see cref="CheckBox"/> in the layout can be clicked,
/// and <see cref="TaskToggled"/> carries the source line back so the tick is
/// written into the text the reader is editing.
/// </summary>
public sealed class MarkdownView : ScrollViewer
{
    private readonly StackPanel _stack = new() { Margin = new Thickness(4, 2, 14, 12) };

    /// <summary>A checkbox was clicked: which source line, and what it now is.</summary>
    public event EventHandler<(int Line, bool Checked)>? TaskToggled;

    public MarkdownView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _stack;
    }

    private string _source = "";

    public string Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value;
            Render();
        }
    }

    private void Render()
    {
        _stack.Children.Clear();

        foreach (var block in Markdown.Parse(_source))
            _stack.Children.Add(Build(block));

        if (_stack.Children.Count == 0)
            _stack.Children.Add(new TextBlock
            {
                Text = "This note is empty.",
                Foreground = Themed("TextMuted", 0x8A, 0x92, 0x9C),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0),
            });
    }

    private UIElement Build(Markdown.Block block) => block switch
    {
        Markdown.Heading h => Build(h),
        Markdown.ListItem l => Build(l),
        Markdown.CodeBlock c => Build(c),
        Markdown.Quote q => Build(q),
        Markdown.Rule => new Border
        {
            Height = 1,
            Background = Themed("BorderSoft", 0x3A, 0x3A, 0x3A),
            Margin = new Thickness(0, 12, 0, 12),
        },
        Markdown.Paragraph p => Text(p.Text, new Thickness(0, 0, 0, 9)),
        _ => new TextBlock(),
    };

    private UIElement Build(Markdown.Heading heading)
    {
        // Sized so the levels stay apart from each other and from body text;
        // below the third they only get weight, since a note rarely goes deeper.
        var (size, top) = heading.Level switch
        {
            1 => (21.0, 4.0),
            2 => (17.0, 16.0),
            3 => (14.5, 14.0),
            _ => (13.0, 12.0),
        };

        var text = Text(heading.Text, new Thickness(0, top, 0, 6));
        text.FontSize = size;
        text.FontWeight = FontWeights.SemiBold;
        text.Foreground = Themed("TextPrimary", 0xEA, 0xEA, 0xEA);

        if (heading.Level > 1) return text;

        // The title of the note gets a rule under it, the way a page does.
        return new StackPanel
        {
            Children =
            {
                text,
                new Border
                {
                    Height = 1,
                    Background = Themed("BorderSoft", 0x3A, 0x3A, 0x3A),
                    Margin = new Thickness(0, 0, 0, 10),
                },
            },
        };
    }

    private UIElement Build(Markdown.ListItem item)
    {
        var row = new DockPanel { Margin = new Thickness(item.Indent * 20, 1, 0, 1) };

        if (item.Checked is { } ticked)
        {
            var box = new CheckBox
            {
                IsChecked = ticked,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0),
                ToolTip = "Tick this off",
            };

            // Report the line, not the new state of the box: the source is the
            // record, and re-rendering from it is what makes the tick stick.
            box.Click += (_, _) => TaskToggled?.Invoke(this, (item.SourceLine, box.IsChecked == true));

            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
        }
        else
        {
            var marker = new TextBlock
            {
                Text = item.Marker ?? "•",
                Foreground = Themed("TextMuted", 0x8A, 0x92, 0x9C),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 0, 8, 0),
                MinWidth = item.Marker is null ? 0 : 18,
            };
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
        }

        var text = Text(item.Text, new Thickness(0));

        // Done is done: a ticked task steps back so the ones left stand out.
        if (item.Checked == true)
        {
            text.Foreground = Themed("TextMuted", 0x8A, 0x92, 0x9C);
            text.TextDecorations = TextDecorations.Strikethrough;
        }

        row.Children.Add(text);
        return row;
    }

    private UIElement Build(Markdown.CodeBlock code) => new Border
    {
        Background = Themed("SurfaceSunken", 0x1E, 0x1E, 0x1E),
        BorderBrush = Themed("BorderSoft", 0x3A, 0x3A, 0x3A),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 4, 0, 10),
        Child = new TextBlock
        {
            Text = code.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Themed("TextPrimary", 0xEA, 0xEA, 0xEA),
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    private UIElement Build(Markdown.Quote quote)
    {
        var text = Text(quote.Text, new Thickness(10, 0, 0, 0));
        text.Foreground = Themed("TextMuted", 0x8A, 0x92, 0x9C);
        text.FontStyle = FontStyles.Italic;

        return new Border
        {
            BorderBrush = Themed("Accent", 0x2E, 0x7D, 0x4F),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 10),
            Child = text,
        };
    }

    /// <summary>One line of styled runs, as a wrapping TextBlock.</summary>
    private TextBlock Text(IReadOnlyList<Markdown.Span> spans, Thickness margin)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            Foreground = Themed("TextPrimary", 0xEA, 0xEA, 0xEA),
            LineHeight = 19,
        };

        foreach (var span in spans)
        {
            if (span.Url is not null) { block.Inlines.Add(Link(span)); continue; }

            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeights.SemiBold;
            if (span.Italic) run.FontStyle = FontStyles.Italic;
            if (span.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (span.Code)
            {
                run.FontFamily = new FontFamily("Consolas");
                run.FontSize = 12;
                run.Background = Themed("SurfaceSunken", 0x1E, 0x1E, 0x1E);
            }
            block.Inlines.Add(run);
        }

        return block;
    }

    private Hyperlink Link(Markdown.Span span)
    {
        var link = new Hyperlink(new Run(span.Text))
        {
            Foreground = Themed("Accent", 0x2E, 0x7D, 0x4F),
            ToolTip = span.Url,
        };

        link.RequestNavigate += (_, _) => { };
        link.Click += (_, _) =>
        {
            // Only the web. A note is the user's own text, but a link is still
            // a string being handed to the shell, and every other scheme —
            // file:, and whatever else has a handler registered — is a way for
            // a click to do something other than open a page.
            if (!Uri.TryCreate(span.Url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* no browser, or the user cancelled — nothing to recover */ }
        };

        return link;
    }

    /// <summary>Palette lookup with a fallback, matching the rest of the suite.</summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
}
