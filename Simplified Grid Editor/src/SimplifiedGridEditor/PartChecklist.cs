using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SimplifiedGridEditor.Core.Packaging;

namespace SimplifiedGridEditor;

/// <summary>
/// A tick per part, with what it holds written beside it.
///
/// Shared by Package and Import because they are one question asked in two
/// directions — which parts of this go out, which parts of that come in — and
/// a list that read differently in the two places would suggest the parts
/// themselves were different.
///
/// Parts nothing has changed are left out rather than shown unticked. A list
/// of eleven rows where two matter is a list nobody reads.
/// </summary>
public static class PartChecklist
{
    /// <summary>The ticks, in the order the parts are declared.</summary>
    public sealed record Boxes(IReadOnlyList<(PackagePart Part, CheckBox Box)> Rows)
    {
        public int Count => Rows.Count;

        public IReadOnlySet<PackagePart> Ticked =>
            Rows.Where(r => r.Box.IsChecked == true).Select(r => r.Part).ToHashSet();
    }

    public static Boxes Build(Panel into, FrameworkElement owner, IReadOnlyDictionary<PackagePart, int> counts)
    {
        var muted = (Brush?)owner.TryFindResource("TextMuted") ?? Brushes.Gray;
        var rows = new List<(PackagePart, CheckBox)>();

        foreach (var part in Enum.GetValues<PackagePart>())
        {
            var count = counts.GetValueOrDefault(part);
            if (count == 0) continue;

            var box = new CheckBox
            {
                IsChecked = true,
                Margin = new Thickness(0, 3, 0, 3),
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new Run($"{PackageParts.Label(part)}  "),
                        new Run($"{count} change(s) — {PackageParts.Hint(part)}") { Foreground = muted },
                    },
                },
            };

            into.Children.Add(box);
            rows.Add((part, box));
        }

        return new Boxes(rows);
    }

    /// <summary>A line for when there is nothing to tick.</summary>
    public static void SayNothing(Panel into, FrameworkElement owner, string text)
        => into.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush?)owner.TryFindResource("TextMuted") ?? Brushes.Gray,
        });
}
