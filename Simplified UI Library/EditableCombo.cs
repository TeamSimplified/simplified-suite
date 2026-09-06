using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimplifiedUi;

/// <summary>
/// Makes an editable ComboBox open the way a plain one does.
///
/// A ComboBox that can be typed into gives the text area to a TextBox, so only
/// the arrow opens the list — a strip about as wide as a scrollbar. That is
/// what Windows does and it has always been fiddly; next to the suite's plain
/// pickers, where the whole box opens, it reads as broken.
///
/// So the first click anywhere on the field opens the list. Once the field has
/// focus, clicks go back to placing the caret, which is what someone who is
/// typing expects. Dragging to select still works either way — the click is
/// handled but not swallowed, so the text box still sees it.
/// </summary>
public static class EditableCombo
{
    /// <summary>
    /// Set on the text box inside the ComboBox template. True makes the first
    /// click on it open the owning ComboBox.
    /// </summary>
    public static readonly DependencyProperty OpenOnFirstClickProperty =
        DependencyProperty.RegisterAttached(
            "OpenOnFirstClick",
            typeof(bool),
            typeof(EditableCombo),
            new PropertyMetadata(false, OnOpenOnFirstClickChanged));

    public static void SetOpenOnFirstClick(DependencyObject element, bool value) =>
        element.SetValue(OpenOnFirstClickProperty, value);

    public static bool GetOpenOnFirstClick(DependencyObject element) =>
        (bool)element.GetValue(OpenOnFirstClickProperty);

    private static void OnOpenOnFirstClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if (e.NewValue is true) box.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Already being typed in: leave the click alone so it places the caret.
        if (box.IsKeyboardFocusWithin) return;

        // Walk up rather than assuming the template's shape, so this still works
        // if the text box is ever nested a level deeper.
        var combo = FindCombo(box);
        if (combo is null || !combo.IsEnabled) return;

        combo.IsDropDownOpen = true;
        // Not handled: the text box still gets the click, so the caret lands
        // where it was pressed and a drag can still select.
    }

    private static ComboBox? FindCombo(DependencyObject from)
    {
        for (var node = from; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is ComboBox combo) return combo;
        return null;
    }
}
