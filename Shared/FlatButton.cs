using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Simplified.Shared;

/// <summary>
/// A button whose fill is the point of it — a colour, or a picture — rather
/// than a surface for a label to sit on.
///
/// Shared rather than copied: the Grid Editor and the Team Editor each had one
/// of these, identical, and the Sponsor Editor's artwork editor wanted the same
/// colour box the Grid Editor asks a team colour with. A colour box that looks
/// like a colour box in one app and like a themed button in another is the kind
/// of difference nobody decides on; it just happens.
/// </summary>
public static class FlatButton
{
    /// <summary>The template. Nothing of the themed button's chrome survives it.</summary>
    public static ControlTemplate Template(double cornerRadius = 4)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.ClipToBoundsProperty, true);
        foreach (var property in new DependencyProperty[]
                 { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })
            border.SetBinding(property, new System.Windows.Data.Binding(property.Name)
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        // The background is the point of these buttons - a team colour, or a
        // logo - so hover brightens the outline rather than the fill, which
        // would misreport the colour underneath the pointer.
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("TextMuted"), "Chrome"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("TextPrimary"), "Chrome"));
        template.Triggers.Add(pressed);

        return template;
    }
}
