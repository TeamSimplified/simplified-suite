using System.Windows;
using System.Windows.Controls;

namespace SimplifiedLanguageEditor;

/// <summary>
/// The dialog for adding one entry: a key, a value, and a reason why not.
///
/// Built in code and wrapped in the shared chrome, which is how dialogs are done
/// across the suite — a XAML window would carry Windows' own title bar, the one
/// thing on screen that stays light when the palette is dark.
/// </summary>
public static class AddRow
{
    /// <summary>
    /// Ask for an entry. Null when the dialog was cancelled.
    /// </summary>
    /// <param name="owner">The window it belongs to.</param>
    /// <param name="taken">
    /// Whether a key is already in the table. Asked rather than handed the whole
    /// table: the answer is the only part of it this dialog has any business
    /// knowing.
    /// </param>
    public static (string Key, string Value)? Show(Window owner, Func<string, bool> taken)
    {
        var root = new StackPanel { Margin = new Thickness(18), Width = 460 };

        root.Children.Add(Label("Key"));
        var keyBox = new TextBox();
        root.Children.Add(keyBox);

        root.Children.Add(Label("Value", first: false));
        var valueBox = new TextBox
        {
            Height = 92,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,

            // The shared TextBox style centres its content, which is right for a
            // one-line field and wrong for something several lines tall.
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        root.Children.Add(valueBox);

        var problem = new TextBlock
        {
            Foreground = Themed("Danger"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(problem);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        // The action first and Cancel last, as every dialog in the suite has
        // them: the first answers Enter and the last answers Esc, so the one
        // that changes nothing is the one on the end.
        var add = new Button
        {
            Content = "Add",
            Padding = new Thickness(18, 4, 18, 4),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(18, 4, 18, 4),
            IsCancel = true,
        };

        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = "Add an entry",
            Content = root,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        add.Click += (_, _) =>
        {
            var key = keyBox.Text.Trim();

            if (key.Length == 0)
            {
                Say(problem, "A key is what the game looks the line up by, so it can't be blank.");
                return;
            }

            if (taken(key))
            {
                Say(problem, $"\"{key}\" is already in this file. Edit it in the table instead — a key " +
                             "can only be in there once.");
                return;
            }

            window.DialogResult = true;
        };

        // After Content, never before: DialogChrome wraps whatever is there at
        // the time, so content assigned afterwards replaces the title bar too.
        SimplifiedUi.DialogChrome.Apply(window);

        window.Loaded += (_, _) => keyBox.Focus();

        return window.ShowDialog() == true ? (keyBox.Text.Trim(), valueBox.Text) : null;
    }

    private static TextBlock Label(string text, bool first = true) => new()
    {
        Text = text,
        Foreground = Themed("TextMuted"),
        Margin = new Thickness(0, first ? 0 : 12, 0, 4),
    };

    private static void Say(TextBlock where, string what)
    {
        where.Text = what;
        where.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A palette brush, looked up each time rather than held: a brush resolved
    /// once at type init is frozen through a theme change.
    /// </summary>
    private static System.Windows.Media.Brush? Themed(string key)
        => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;
}
