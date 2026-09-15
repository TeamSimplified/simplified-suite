using System.Windows;

namespace SimplifiedUi;

/// <summary>
/// Which row a drag is currently over.
///
/// A drop onto a list has to say where it will land before the mouse button
/// comes up, and the row under the pointer is not the selected row — selection
/// belongs to the person, and moving it while they drag would leave their
/// choice changed if they let go somewhere else.
///
/// An attached property rather than a brush set from code: the row templates
/// build their chrome themselves and hardcode its background, so the only way
/// in from outside is a state the template already knows how to draw.
///
/// Named for the highlight rather than for the drop, because Studio already has
/// a DropTarget of its own for dragging parts between categories, and a name
/// that resolves to two different things inside one window is a name that will
/// be read wrong at least once.
/// </summary>
public static class DropHighlight
{
    public static readonly DependencyProperty IsOverProperty =
        DependencyProperty.RegisterAttached(
            "IsOver", typeof(bool), typeof(DropHighlight), new PropertyMetadata(false));

    public static void SetIsOver(DependencyObject element, bool value) =>
        element.SetValue(IsOverProperty, value);

    public static bool GetIsOver(DependencyObject element) =>
        (bool)element.GetValue(IsOverProperty);
}
