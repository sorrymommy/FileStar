using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FileStar.Views;

/// <summary>Turns the menus a ViewModel describes (<see cref="MenuItemViewModel"/>) into WPF menu items.</summary>
public static class MenuBuilder
{
    public static void Fill(ItemCollection target, IEnumerable<MenuItemViewModel> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(Create(item));
    }

    public static Control Create(MenuItemViewModel item)
    {
        if (item.IsSeparator)
            return new Separator();
        var menuItem = new MenuItem
        {
            Header = item.Header.Replace("_", "__"),   // names are shown as typed, not as access keys
            InputGestureText = item.Gesture,
            ToolTip = item.ToolTip,
            IsEnabled = item.IsEnabled,
            Command = item.Command,
        };
        if (item.Icon != null)
            menuItem.Icon = new System.Windows.Controls.Image { Source = item.Icon, Width = 16, Height = 16 };
        if (item.Children != null)
            Fill(menuItem.Items, item.Children);
        return menuItem;
    }

    /// <summary>Makes an existing menu item (declared in XAML) show <paramref name="item"/>.</summary>
    public static void Apply(MenuItem target, MenuItemViewModel item)
    {
        target.Header = item.Header.Replace("_", "__");
        target.Command = item.Children == null ? item.Command : null;
        target.IsEnabled = item.IsEnabled;
        target.InputGestureText = item.Gesture;
        target.Items.Clear();
        if (item.Children != null)
            Fill(target.Items, item.Children);
    }

    /// <summary>Shows <paramref name="items"/> as a drop-down under <paramref name="target"/> (in the window's text font, not a button's icon font).</summary>
    public static void ShowBelow(System.Windows.FrameworkElement target, IEnumerable<MenuItemViewModel> items, FontFamily font, double fontSize)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom, FontFamily = font, FontSize = fontSize };
        Fill(menu.Items, items);
        menu.IsOpen = true;
    }
}
