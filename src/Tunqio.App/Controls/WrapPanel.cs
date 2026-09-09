using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Tunqio.App.Controls;

/// <summary>
/// Lays children out left to right and wraps at the available width, each line as tall as its tallest child
/// (the Genres tag cloud). WinUI 3 ships no wrapping layout for variable-width items, and the cloud is small
/// enough not to need virtualisation.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(4.0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    /// <summary>The gap between neighbours and between lines.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, width = 0, height = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size size = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + Spacing + size.Width > availableSize.Width)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight + Spacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? Spacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(Math.Max(width, lineWidth), height + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (UIElement child in Children)
        {
            Size size = child.DesiredSize;
            if (x > 0 && x + Spacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + Spacing;
                lineHeight = 0;
            }

            if (x > 0)
            {
                x += Spacing;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
