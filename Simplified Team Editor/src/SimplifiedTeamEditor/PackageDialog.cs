using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimplifiedTeamEditor.Core.Packaging;
using SimplifiedTeamEditor.Util;
using SimplifiedUi;

namespace SimplifiedTeamEditor;

/// <summary>
/// Everything a package needs, asked once.
///
/// Laid out like Studio's export: a label column, the save location with a
/// Browse beside it, then the fields that go into the manifest. It used to ask
/// for the name here and the folder in a second dialog afterwards, which meant
/// answering half a question, pressing on, and finding the rest of it.
///
/// The name is the only one that has to be filled in — it is what the package
/// is listed as wherever mods are installed. Author and version are stamped
/// into the manifest when given and left out when not.
/// </summary>
public static class PackageDialog
{
    /// <summary>What the user asked for, or null when they cancelled.</summary>
    /// <param name="Path">The .zip to write, folder and file name together.</param>
    public sealed record Request(string Path, ModInfo Info);

    public static Request? Show(Window owner, string? defaultAuthor, string? lastFolder)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
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

        // Row 0: where it goes. Read-only, as Studio has it — the folder is
        // picked rather than typed, so a path that isn't there can't be entered.
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

        var nameBox = Field("Mod name:", 1, "", span: 2);
        var authorBox = Field("Mod author:", 2, defaultAuthor ?? "", span: 2);
        var versionBox = Field("Mod version:", 3, "1.0", span: 2);

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
            IsEnabled = false,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        buttons.Children.Add(package);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        // Both halves are needed to write anything, so Package waits for both
        // rather than taking one and complaining about the other afterwards.
        void Refresh() =>
            package.IsEnabled = locBox.Text.Length > 0 && nameBox.Text.Trim().Length > 0;

        nameBox.TextChanged += (_, _) => Refresh();

        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog
            {
                Title = "Choose where to save the package",
                InitialDirectory = locBox.Text.Length > 0 ? locBox.Text : "",
            };
            if (picker.ShowDialog(owner) != true) return;
            locBox.Text = picker.FolderName;
            Refresh();
        };

        var window = new Window
        {
            Title = "Package team edits",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        SimplifiedUi.DialogChrome.Apply(window);
        package.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => nameBox.Focus();

        Refresh();
        if (window.ShowDialog() != true) return null;

        var name = nameBox.Text.Trim();
        var info = new ModInfo(
            name,
            authorBox.Text.Trim() is { Length: > 0 } a ? a : null,
            versionBox.Text.Trim() is { Length: > 0 } v ? v : null);

        return new Request(Path.Combine(locBox.Text, SafeFileName(name) + ".zip"), info);
    }

    /// <summary>The mod name as a file name, with what the filesystem refuses taken out.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "team-edits" : cleaned;
    }
}
