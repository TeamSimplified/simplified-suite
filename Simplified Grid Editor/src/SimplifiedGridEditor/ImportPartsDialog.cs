using System.Windows;
using System.Windows.Controls;
using SimplifiedGridEditor.Core.Packaging;
using SimplifiedUi;

namespace SimplifiedGridEditor;

/// <summary>
/// What a file somebody handed us would change, and which of it to take.
///
/// The same list Package shows, read the other way: there it is what goes out,
/// here what comes in. One part at a time matters as much here as there — a mod
/// whose colours are worth having and whose grid is not is the usual case, and
/// a whole-file import would be all or nothing.
/// </summary>
public static class ImportPartsDialog
{
    public static IReadOnlySet<PackagePart>? Show(
        Window owner, string heading, IReadOnlyDictionary<PackagePart, int> counts)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = heading,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var choices = new StackPanel();
        var boxes = PartChecklist.Build(choices, owner, counts);

        var scroller = new ScrollViewer
        {
            Content = choices,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 360,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var import = new Button
        {
            Content = "Import",
            IsDefault = true,
            Width = 100,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = boxes.Count > 0,
        };
        buttons.Children.Add(import);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Width = 80 });
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        var window = new Window
        {
            Title = "Import",
            Content = grid,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        DialogChrome.Apply(window);
        import.Click += (_, _) => window.DialogResult = true;

        return window.ShowDialog() == true ? boxes.Ticked : null;
    }
}
