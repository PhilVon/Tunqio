using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Tunqio.App.Controls;

internal static class VisualTree
{
    /// <summary>Depth-first search for the first descendant of <typeparamref name="T"/> (the ListView's ScrollViewer, say).</summary>
    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
