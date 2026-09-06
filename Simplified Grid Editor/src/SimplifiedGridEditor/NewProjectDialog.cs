using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedGridEditor.Core.Project;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// What a new project needs, in Studio's order: where the <c>.simplified</c>
/// goes, then what the mod inside it is called.
///
/// The two are separate on purpose. The project file is a working document and
/// its name is whatever is convenient on disk; the mod name is what the package
/// is called and what anyone installing it sees. Asking once and using the
/// answer for both is how a mod ends up published as "Test".
///
/// Studio's equivalent also asks which <c>.serps</c> template to start from,
/// because a Studio project is whichever car parts that template covers. A grid
/// project is always the same three things, so there is nothing to choose.
/// </summary>
public static class NewProjectDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    public sealed record Request(string SavePath, string ModName, string ModAuthor);

    public static Request? Show(Window owner, string? defaultAuthor, string? lastFolder)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Label(string text, int row)
        {
            var block = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, 0);
            grid.Children.Add(block);
        }

        TextBox Field(string label, int row, string value, int span)
        {
            Label(label, row);
            var box = new TextBox
            {
                Text = value,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 6, 0, 6),
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetColumnSpan(box, span);
            grid.Children.Add(box);
            return box;
        }

        // Read-only, as Studio has it — the path is picked rather than typed, so
        // a folder that isn't there can't be entered.
        var saveBox = Field("Save project to:", 0, "", span: 1);
        saveBox.IsReadOnly = true;
        saveBox.Themed(Control.BackgroundProperty, "SurfaceAlt");

        var browse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 6, 0, 6),
        };
        Grid.SetRow(browse, 0);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        var nameBox = Field("Mod name:", 1, "", span: 2);
        var authorBox = Field("Mod author:", 2, defaultAuthor ?? "", span: 2);

        var note = new TextBlock
        {
            Text = "The project starts from the game's own files — the database, the eleven language " +
                   "files and the UI archives — so it carries your edits and nothing else. The mod " +
                   "name is what the package is called.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        }.Themed(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(note, 3);
        Grid.SetColumn(note, 1);
        Grid.SetColumnSpan(note, 2);
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
        Grid.SetRow(buttons, 4);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        // Both are needed: one says where the project goes, the other what the
        // mod is called, and neither can stand in for the other.
        void Refresh() =>
            create.IsEnabled = saveBox.Text.Length > 0 && nameBox.Text.Trim().Length > 0;

        nameBox.TextChanged += (_, _) => Refresh();

        browse.Click += (_, _) =>
        {
            var picker = new SaveFileDialog
            {
                Title = "Save the project as",
                Filter = $"Simplified projects (*{ProjectStore.ProjectFileExtension})|*{ProjectStore.ProjectFileExtension}",
                DefaultExt = ProjectStore.ProjectFileExtension,
                // The mod name is a reasonable first guess at the file name, and
                // still only a guess — the picker is where it is settled.
                FileName = SafeFileName(nameBox.Text.Trim()),
                InitialDirectory = lastFolder is { Length: > 0 } && Directory.Exists(lastFolder) ? lastFolder : "",
            };
            if (picker.ShowDialog(owner) != true) return;
            saveBox.Text = picker.FileName;
            Refresh();
        };

        var window = new Window
        {
            Title = "New project",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 580,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        create.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => nameBox.Focus();

        if (window.ShowDialog() != true) return null;
        return new Request(saveBox.Text, nameBox.Text.Trim(), authorBox.Text.Trim());
    }

    /// <summary>A mod name as a file name, with what the filesystem refuses taken out.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "grid" : cleaned;
    }
}
