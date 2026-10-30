using System.Windows;
using System.Windows.Media;

namespace FileStar.Views;

public static class ViewExtensions
{
    /// <summary>Binds a property to a theme color (Th.*) so it follows light/dark switches; returns the element.</summary>
    public static T WithResource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    /// <summary>The nearest visual (or logical) ancestor of <paramref name="start"/> that is a <typeparamref name="T"/>, or start itself.</summary>
    public static T? FindAncestor<T>(this DependencyObject? start) where T : DependencyObject
    {
        for (var node = start; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T match)
                return match;
        }
        return null;
    }

    public static T? FindVisualChild<T>(this DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is T nested)
                return nested;
        }
        return null;
    }
}
