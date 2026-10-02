using System.IO;
using System.Windows;
using System.Windows.Controls;
using SimplifiedTeamEditor.Core.Logos;
using SimplifiedUi;
using Season = SimplifiedTeamEditor.Core.Teams.Season;

namespace SimplifiedGridEditor;

/// <summary>
/// What an archive carries, and which of it to take.
///
/// Grouped by team rather than by logo. An archive usually holds the same
/// team's badge, team-select and starting-grid picture together — they are
/// three arrays of one design — and nobody wants two of the three, so the tick
/// is on the team and the kinds are listed beside it.
///
/// Both grids at once, because an archive holds both and the slice says which
/// is which. The year is in the group heading so taking one and not the other
/// is a question of unticking a group.
/// </summary>
public static class ImportLogosDialog
{
    public static IReadOnlyList<FoundLogo>? Show(Window owner, LogoSource source)
    {
        var body = new StackPanel();
        var boxes = new List<(CheckBox Box, List<FoundLogo> Logos)>();

        foreach (var season in new[] { Season.Y2026, Season.Y2025 })
        {
            var forSeason = source.Logos.Where(l => l.Season == season).ToList();
            if (forSeason.Count == 0) continue;

            body.Children.Add(new TextBlock
            {
                Text = season == Season.Y2026 ? "2026" : "2025",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, body.Children.Count > 0 ? 14 : 0, 0, 6),
            });

            // In grid order, so the list reads like the Teams tab rather than
            // like the array's own ordering, which is nobody's idea of an order.
            foreach (var group in forSeason
                         .GroupBy(l => l.TeamId, StringComparer.Ordinal)
                         .OrderBy(g => g.First().Team?.Label ?? g.Key, StringComparer.Ordinal))
            {
                var logos = group.ToList();
                var label = logos[0].Team?.Label ?? group.Key;

                var box = new CheckBox
                {
                    IsChecked = true,
                    Margin = new Thickness(0, 3, 0, 3),
                    Content = new TextBlock
                    {
                        Inlines =
                        {
                            new System.Windows.Documents.Run($"{label}  "),
                            new System.Windows.Documents.Run(
                                string.Join(", ", logos.Select(l => l.Kind.Label.ToLowerInvariant())))
                            {
                                Foreground = (System.Windows.Media.Brush?)owner.TryFindResource("TextMuted")
                                             ?? System.Windows.Media.Brushes.Gray,
                            },
                        },
                    },
                };

                body.Children.Add(box);
                boxes.Add((box, logos));
            }
        }

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = Summarise(source),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        grid.Children.Add(heading);

        var scroller = new ScrollViewer
        {
            Content = body,
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
        var import = new Button { Content = "Import", IsDefault = true, Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Width = 80 });
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        var window = new Window
        {
            Title = "Import logos",
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

        if (window.ShowDialog() != true) return null;

        return boxes.Where(b => b.Box.IsChecked == true).SelectMany(b => b.Logos).ToList();
    }

    /// <summary>
    /// What was found, in one line. Said plainly because the interesting cases
    /// are the empty ones, and they mean different things: the wrong archive,
    /// or the right one with the game's own pictures still in it.
    /// </summary>
    public static string Summarise(LogoSource source)
    {
        var name = Path.GetFileName(source.Path);

        if (source.Arrays.Count == 0)
            return $"{name} holds none of the game's team logo arrays, so there are no logos in it to take.";

        var arrays = $"{source.Arrays.Count} of the game's {TeamLogos.Kinds.Count} logo arrays";

        if (source.Logos.Count == 0)
            return source.Unclaimed > 0
                ? $"{name} has {arrays}, and nothing in them differs from the game's own except "
                  + $"{source.Unclaimed} slice(s) that belong to no team on either grid."
                : $"{name} has {arrays}, and every logo in them is the game's own.";

        var line = $"{name} has {arrays}, carrying {source.Logos.Count} logo(s) that differ from the game's.";

        if (source.Unclaimed > 0)
            line += $" {source.Unclaimed} more differ in slices that belong to no team on either grid, "
                  + "and are left where they are.";

        return line;
    }
}
