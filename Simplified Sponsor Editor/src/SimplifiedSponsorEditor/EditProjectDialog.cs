using System.Windows;
using System.Windows.Controls;
using SimplifiedSponsorEditor.Core.Project;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// What the mod inside this project is called, after the fact.
///
/// The same two fields the new-project dialog asks for, minus the path — a
/// project that already exists is renamed with Save as, not here. Studio has
/// the same button for the same reason: the answers given once at creation are
/// the ones most likely to want changing later.
/// </summary>
public static class EditProjectDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    public sealed record Request(string ModName, string ModAuthor);

    public static Request? Show(Window owner, ProjectInfo info)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBox Field(string label, int row, string value)
        {
            var block = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, 0);
            grid.Children.Add(block);

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

        var nameBox = Field("Mod name:", 0, info.Name);
        var authorBox = Field("Mod author:", 1, info.Author);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = info.Name.Trim().Length > 0,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        // The mod name is what the package is called, so it cannot be blank.
        nameBox.TextChanged += (_, _) => save.IsEnabled = nameBox.Text.Trim().Length > 0;

        var window = new Window
        {
            Title = "Edit project",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        save.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => nameBox.Focus();

        if (window.ShowDialog() != true) return null;
        return new Request(nameBox.Text.Trim(), authorBox.Text.Trim());
    }
}
