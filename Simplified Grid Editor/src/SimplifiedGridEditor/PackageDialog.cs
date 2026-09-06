using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// Everything a package needs, asked once: where it goes and which version it
/// is.
///
/// Shorter than the Team Editor's, which also asks for a name and an author —
/// here the project already carries both, so asking again would be asking the
/// same question twice and letting the two answers drift.
/// </summary>
public static class PackageDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    /// <param name="Path">The .zip to write, folder and file name together.</param>
    public sealed record Request(string Path, string Version);

    public static Request? Show(Window owner, string projectName, string? version, string? lastFolder)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

        // Read-only, as Studio has it — the folder is picked rather than typed,
        // so a path that isn't there can't be entered.
        var folder = lastFolder is { Length: > 0 } && Directory.Exists(lastFolder) ? lastFolder : "";
        var locBox = Field("Save location:", 0, folder, span: 1);
        locBox.IsReadOnly = true;
        locBox.Themed(Control.BackgroundProperty, "SurfaceAlt");

        var browse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 6, 0, 6),
        };
        Grid.SetRow(browse, 0);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        var versionBox = Field("Mod version:", 1, version is { Length: > 0 } ? version : "1.0", span: 2);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var package = new Button
        {
            Content = "Package",
            IsDefault = true,
            Width = 100,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = folder.Length > 0,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(package);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog
            {
                Title = "Choose where to save the package",
                InitialDirectory = locBox.Text.Length > 0 ? locBox.Text : "",
            };
            if (picker.ShowDialog(owner) != true) return;
            locBox.Text = picker.FolderName;
            package.IsEnabled = true;
        };

        var window = new Window
        {
            Title = $"Package {projectName}",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        package.Click += (_, _) => window.DialogResult = true;

        if (window.ShowDialog() != true) return null;

        return new Request(
            System.IO.Path.Combine(locBox.Text, SafeFileName(projectName) + ".zip"),
            versionBox.Text.Trim());
    }

    /// <summary>The project name as a file name, with what the filesystem refuses taken out.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "grid" : cleaned;
    }
}
