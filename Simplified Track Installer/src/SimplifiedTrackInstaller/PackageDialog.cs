using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using SimplifiedTrackInstaller.Core.Track;
using SimplifiedUi;

namespace SimplifiedTrackInstaller;

/// <summary>
/// Everything a package needs, asked once: where it goes, as a zip or a folder,
/// and which version it is. The Grid Editor's dialog, less its list of parts —
/// a track is one thing, packaged whole.
/// </summary>
public static class PackageDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    /// <param name="Path">The .zip to write, or the folder, folder and name together.</param>
    /// <param name="AsZip">A .zip, or a folder of the same files at the same paths.</param>
    public sealed record Request(string Path, string Version, bool AsZip);

    public static Request? Show(Window owner, string modName, string? version, string? lastFolder)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Label(string text, int row)
        {
            var block = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 8, 10),
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
                Margin = new Thickness(0, 10, 0, 10),
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
            Margin = new Thickness(8, 10, 0, 10),
        };
        Grid.SetRow(browse, 0);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        var versionBox = Field("Mod version:", 2, version is { Length: > 0 } ? version : "1.0", span: 2);

        // Zip or folder. A mod manager wants the one, somebody dropping files
        // into their game wants the other, and the contents are identical.
        var asZip = new ToggleButton
        {
            Content = "Zip",
            IsChecked = true,
            Style = (Style)owner.FindResource("FilterChip"),
            Margin = new Thickness(0, 10, 0, 10),
            ToolTip = "One .zip file, which is what a mod manager installs",
        };
        var asFolder = new ToggleButton
        {
            Content = "Folder",
            Style = (Style)owner.FindResource("FilterChip"),
            Margin = new Thickness(6, 10, 0, 10),
            ToolTip = "A folder of the same files, to drop into the game yourself",
        };

        asZip.Checked += (_, _) => asFolder.IsChecked = false;
        asFolder.Checked += (_, _) => asZip.IsChecked = false;

        // One of them is always on: clicking the lit one again would otherwise
        // leave the question unanswered.
        asZip.Unchecked += (_, _) => { if (asFolder.IsChecked != true) asZip.IsChecked = true; };
        asFolder.Unchecked += (_, _) => { if (asZip.IsChecked != true) asFolder.IsChecked = true; };

        Label("Format:", 1);

        var formats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { asZip, asFolder },
        };
        Grid.SetRow(formats, 1);
        Grid.SetColumn(formats, 1);
        Grid.SetColumnSpan(formats, 2);
        grid.Children.Add(formats);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
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
        Grid.SetRow(buttons, 3);
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
            Title = "Package track",
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

        var chosen = versionBox.Text.Trim();
        var zipped = asZip.IsChecked == true;

        return new Request(
            System.IO.Path.Combine(locBox.Text, TrackPackager.PackageFileName(modName, chosen) + (zipped ? ".zip" : "")),
            chosen,
            zipped);
    }
}
