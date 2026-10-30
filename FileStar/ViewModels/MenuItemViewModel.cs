using System.Windows.Input;
using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>
/// One entry of a menu whose contents depend on the situation (favorites, recent items, the tree's right-click
/// menu). The ViewModel decides what is in it; the view turns it into WPF menu items.
/// </summary>
public sealed class MenuItemViewModel
{
    public static readonly MenuItemViewModel Separator = new() { IsSeparator = true };

    public string Header { get; init; } = "";
    public ICommand? Command { get; init; }
    public string Gesture { get; init; } = "";
    public string? ToolTip { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsSeparator { get; private init; }
    public IReadOnlyList<MenuItemViewModel>? Children { get; init; }

    /// <summary>A plain entry that runs <paramref name="action"/>.</summary>
    public static MenuItemViewModel Action(string header, Action action, bool enabled = true, string gesture = "") =>
        new() { Header = header, Command = new RelayCommand(action), IsEnabled = enabled, Gesture = gesture };

    /// <summary>A greyed-out line of text (a hint, or "(없음)").</summary>
    public static MenuItemViewModel Note(string header) => new() { Header = header, IsEnabled = false };
}
