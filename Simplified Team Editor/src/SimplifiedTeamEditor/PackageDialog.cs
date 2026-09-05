using System.Windows;
using System.Windows.Controls;
using SimplifiedTeamEditor.Core.Packaging;
using SimplifiedTeamEditor.Util;
using SimplifiedUi;

namespace SimplifiedTeamEditor;

/// <summary>
/// What a package should call itself before it is written.
///
/// The name is what the package will be listed as wherever mods are installed,
/// so it is the one field that has to be filled in. Author and version are
/// stamped into the manifest when given and left out when not.
/// </summary>
public static class PackageDialog
{
    public static ModInfo? Show(Window owner, string? defaultAuthor)
    {
        var window = new Window
        {
            Title = "Package team edits",
            Owner = owner,
            SizeToContent = SizeToContent.Height,
            Width = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };

        var body = new StackPanel { Margin = new Thickness(18) };

        TextBox Field(string label, string value, string hint)
        {
            body.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 12, 0, 4),
            }.Themed(TextBlock.ForegroundProperty, "TextMuted"));

            var box = new TextBox { Text = value, Tag = hint };
            body.Children.Add(box);
            return box;
        }

        var name = Field("Mod name", "", "The name your mod is listed by");
        var author = Field("Author", defaultAuthor ?? "", "Your name");
        var version = Field("Version", "1.0", "");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var ok = new Button { Content = "Choose file…", MinWidth = 110, IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        name.TextChanged += (_, _) => ok.IsEnabled = name.Text.Trim().Length > 0;
        ok.Click += (_, _) => window.DialogResult = true;

        window.Content = body;
        SimplifiedUi.DialogChrome.Apply(window);
        window.Loaded += (_, _) => name.Focus();

        if (window.ShowDialog() != true) return null;

        return new ModInfo(name.Text.Trim(),
                           author.Text.Trim() is { Length: > 0 } a ? a : null,
                           version.Text.Trim() is { Length: > 0 } v ? v : null);
    }
}
