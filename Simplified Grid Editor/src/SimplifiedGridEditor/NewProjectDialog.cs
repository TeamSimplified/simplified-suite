using System.Windows;
using System.Windows.Controls;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// What a new project needs, which is only what to call it.
///
/// Studio's equivalent also asks which <c>.serps</c> template to start from,
/// because a Studio project is whichever car parts that template covers. A grid
/// project is always the same three things — the database, the language files
/// and the UI archives — so there is nothing to choose.
/// </summary>
public static class NewProjectDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    public sealed record Request(string Name, string Author);

    public static Request? Show(Window owner, string? defaultAuthor)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBox Field(string label, int row, string value)
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6),
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var box = new TextBox
            {
                Text = value,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 6, 0, 6),
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        var nameBox = Field("Project name:", 0, "");
        var authorBox = Field("Author:", 1, defaultAuthor ?? "");

        var note = new TextBlock
        {
            Text = "The project starts from the game's own files — the database, the eleven language " +
                   "files and the UI archives — so it carries your edits and nothing else.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(note, 2);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var create = new Button
        {
            Content = "Create",
            IsDefault = true,
            Width = 100,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        // The name is what the project file and the package are called, so it is
        // the one field that has to be filled in.
        nameBox.TextChanged += (_, _) => create.IsEnabled = nameBox.Text.Trim().Length > 0;

        var window = new Window
        {
            Title = "New project",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        create.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => nameBox.Focus();

        if (window.ShowDialog() != true) return null;
        return new Request(nameBox.Text.Trim(), authorBox.Text.Trim());
    }
}
