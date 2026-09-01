using System;
using System.Windows;

namespace SimplifiedUi;

/// <summary>
/// Which palette the app is wearing, and how to change it while it runs.
///
/// The palette is the first dictionary App.xaml merges, ahead of Theme.xaml —
/// swapping that one entry re-colours everything, because every style names its
/// colours through palette keys and resolves them with DynamicResource. Order
/// matters on the way in and not on the way back: Theme.xaml's dynamic lookups
/// find whatever is in the dictionary at the time.
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>The palette currently merged. Light until something says otherwise.</summary>
    public static string Current { get; private set; } = Light;

    public static bool IsDark => Current == Dark;

    /// <summary>
    /// Swap the palette. Anything unrecognised is treated as Light rather than
    /// left in a half-applied state — a config file can hold anything.
    /// </summary>
    public static void Apply(string? theme)
    {
        var wanted = theme == Dark ? Dark : Light;
        var app = Application.Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count == 0) return;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SimplifiedUi;component/Palette.{wanted}.xaml",
                             UriKind.Absolute),
        };

        // Replace in place rather than clear-and-add: the palette has to stay
        // ahead of Theme.xaml, and removing it first would briefly leave every
        // dynamic lookup unresolved.
        dictionaries[0] = palette;
        Current = wanted;
    }
}
