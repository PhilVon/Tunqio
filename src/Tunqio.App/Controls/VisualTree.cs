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

    /// <summary>
    /// Every visible descendant of <typeparamref name="T"/>, in tree order. What it is for is asking the live tree
    /// what it is showing rather than asking a view model what it believes — the gap between the two is the whole
    /// class of bug where a property is right and nothing is bound to it.
    /// </summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }

            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
