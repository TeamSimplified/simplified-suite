using System.Windows;

namespace SimplifiedUi;

/// <summary>
/// Painting code-built UI from the palette in a way that keeps following it.
/// </summary>
public static class ThemedElement
{
    /// <summary>
    /// Point a property at a palette key and leave it pointed there — the code
    /// equivalent of DynamicResource in XAML.
    ///
    /// Prefer this to assigning a brush for anything that can still be on screen
    /// when the palette changes. A resolved brush is a value: it is correct when
    /// assigned and never hears about the swap. Settings is the case that proves
    /// it, being the one dialog open while the theme is being changed.
    ///
    /// Returns the element, so it chains onto an object initialiser rather than
    /// forcing the construction to be broken up.
    /// </summary>
    public static T Themed<T>(this T element, DependencyProperty property, string key)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
