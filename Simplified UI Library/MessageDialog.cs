using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimplifiedUi;

/// <summary>
/// The suite's message box: a headline, a muted body, and a right-aligned row
/// of named buttons, themed along with the window that owns it.
///
/// It replaces <c>MessageBox.Show</c> everywhere. The system one draws itself
/// from Windows' own colours, so in the dark palette it arrives as a white
/// rectangle with a system icon — the one thing on screen that doesn't belong
/// to the app. Studio grew this shape first; it lives here now so every app
/// gets it rather than the two that were written last.
///
/// <see cref="Show"/> takes the same arguments in the same order as
/// <c>MessageBox.Show</c> and answers with the same
/// <see cref="MessageBoxResult"/>, so a call site only loses the class name.
/// </summary>
public static class MessageDialog
{
    /// <summary>
    /// Stand-in for <c>MessageBox.Show</c>.
    ///
    /// These messages were already written as a headline, a blank line, then
    /// the detail, so the first paragraph becomes the headline and the rest the
    /// body — the shape every other dialog here uses.
    ///
    /// The icon argument is kept for the call sites' sake but only Error is
    /// expressed, as a red headline. A warning icon on what is really a
    /// confirmation reads as alarm.
    /// </summary>
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        // MessageBox.Show tolerated being called off the UI thread; a Window does
        // not. Several of these report the outcome of background work, so marshal
        // rather than leave that to each call site.
        var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(owner, message, title, buttons, icon));

        var split = message.IndexOf("\n\n", StringComparison.Ordinal);
        var headline = (split < 0 ? message : message[..split]).Trim();
        var body = split < 0 ? "" : message[(split + 2)..].Trim('\n');

        var choices = buttons switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                ("OK", MessageBoxResult.OK),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxButton.YesNo => new[]
            {
                ("Yes", MessageBoxResult.Yes),
                ("No", MessageBoxResult.No),
            },
            MessageBoxButton.YesNoCancel => new[]
            {
                ("Yes", MessageBoxResult.Yes),
                ("No", MessageBoxResult.No),
                ("Cancel", MessageBoxResult.Cancel),
            },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };

        return Choose(owner, title, headline, body, icon == MessageBoxImage.Error, choices);
    }

    /// <summary>
    /// A question with buttons that say what they do, which is what lets the
    /// body drop the "Yes — save, No — discard" gloss a message box needs.
    ///
    /// Choices are given in reading order. The first answers Enter; the last
    /// answers Esc and the title-bar close, so it should always be the choice
    /// that changes nothing.
    /// </summary>
    public static MessageBoxResult Choose(Window? owner, string title, string headline, string body,
        params (string Label, MessageBoxResult Result)[] choices)
        => Choose(owner, title, headline, body, danger: false, choices);

    /// <inheritdoc cref="Choose(Window?, string, string, string, ValueTuple{string, MessageBoxResult}[])"/>
    /// <param name="danger">Colour the headline as a problem rather than a question.</param>
    public static MessageBoxResult Choose(Window? owner, string title, string headline, string body,
        bool danger, params (string Label, MessageBoxResult Result)[] choices)
    {
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = headline,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed(danger ? "Danger" : "TextPrimary",
                                danger ? Brushes.Firebrick : Brushes.Black),
            Margin = new Thickness(0, 0, 0, 8),
        });

        // Nothing to say takes no room, rather than an empty line above the
        // buttons where the detail would have been.
        if (body.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                // Palette lookup rather than a literal grey, so the body text
                // follows the theme instead of staying light-mode grey.
                Foreground = Themed("TextMuted", Brushes.Gray),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var window = new Window
        {
            Title = title,
            Content = root,
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var picked = (MessageBoxResult?)null;
        for (var i = 0; i < choices.Length; i++)
        {
            var (label, result) = choices[i];
            var last = i == choices.Length - 1;
            var button = new Button
            {
                Content = label,
                MinWidth = 110,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 18, last ? 0 : 8, 0),
                IsDefault = i == 0,
                IsCancel = last,
            };
            // Only the others close the window themselves — the last one is left
            // to the built-in cancel handling, which would fault if it tried to
            // set a result on an already-closed window.
            if (!last) button.Click += (_, _) => { picked = result; window.DialogResult = true; };
            row.Children.Add(button);
        }
        root.Children.Add(row);

        window.ShowDialog();
        return picked ?? choices[^1].Result;
    }

    /// <summary>
    /// Palette lookup for the two brushes this paints in code. Goes through
    /// Application.Current: every palette key lives in application resources,
    /// which is where a code-built dialog can see them anyway.
    /// </summary>
    private static Brush Themed(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
