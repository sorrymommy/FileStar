using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStar.Views;

// "탐색기 메뉴": Windows Explorer's own right-click menu for the selection, a tree folder, or the folder background.
public partial class MainWindow
{
    readonly DispatcherTimer _windowsMenuHover = new() { Interval = TimeSpan.FromMilliseconds(200) };
    ShellContextMenu.PreparedMenu? _preparedShellMenu;

    /// <summary>An icon-font glyph whose color follows the theme.</summary>
    TextBlock ThemedGlyph(string glyph, string brushKey, double size) =>
        new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = glyph, FontSize = size }
            .WithResource(TextBlock.ForegroundProperty, brushKey);

    /// <summary>"탐색기 메뉴 ▸": opens the Explorer menu beside it on click, or after hovering like a submenu.</summary>
    MenuItem CreateWindowsMenuItem(Action<Point> open)
    {
        var item = new MenuItem
        {
            Header = "탐색기 메뉴",
            InputGestureText = "▸",
            ToolTip = "Windows 탐색기의 오른쪽 클릭 메뉴 (Shift+오른쪽 클릭으로 바로 열기)",
            Icon = ThemedGlyph("", "Th.Accent", 13),
            Tag = open,
        };
        item.Click += WindowsMenuItem_Click;
        item.MouseEnter += WindowsMenuItem_MouseEnter;
        item.MouseLeave += WindowsMenuItem_MouseLeave;
        return item;
    }

    void OpenWindowsMenuFrom(MenuItem item)
    {
        _windowsMenuHover.Stop();
        if (!item.IsVisible)
            return;
        var point = item.PointToScreen(new Point(item.ActualWidth, 0));   // like a submenu: to the right of the item
        var open = item.Tag as Action<Point> ?? (p => ShowWindowsMenuForSelection(p, extended: false));
        if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu contextMenu)
            contextMenu.IsOpen = false;
        // Let the WPF menu close before the native one takes over the mouse.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => open(point));
    }

    void WindowsMenuItem_Click(object sender, RoutedEventArgs e) => OpenWindowsMenuFrom((MenuItem)sender);

    void WindowsMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        _windowsMenuHover.Stop();
        _windowsMenuHover.Tag = sender;
        _windowsMenuHover.Start();
    }

    void WindowsMenuItem_MouseLeave(object sender, MouseEventArgs e) => _windowsMenuHover.Stop();

    void WindowsMenuHover_Tick(object? sender, EventArgs e)
    {
        _windowsMenuHover.Stop();
        if (_windowsMenuHover.Tag is MenuItem { IsMouseOver: true } item)
            OpenWindowsMenuFrom(item);
    }

    /// <summary>Explorer menu for the selected files, or for the folder background when nothing is selected.</summary>
    void ShowWindowsMenuForSelection(Point screenPoint, bool extended)
    {
        var paths = SelectedRows.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
            ShowWindowsBackgroundMenu(screenPoint, extended);
        else
            ShowWindowsMenu(paths, screenPoint, extended);
    }

    // ── Building the Windows menu ahead of time ──
    // Building it is the slow part (~0.3 s: every installed extension adds items). While FileStar's own menu is
    // open, the user still has to move to "탐색기 메뉴", so it is built then and shown instantly on arrival.

    void PrepareShellMenu(IReadOnlyList<string>? paths)
    {
        DiscardPreparedShellMenu();
        if (paths is not { Count: > 0 } && _vm.InVirtual)
            return;
        try
        {
            _preparedShellMenu = paths is { Count: > 0 }
                ? ShellContextMenu.PrepareForItems(this, paths, extended: false)
                : ShellContextMenu.PrepareForFolderBackground(this, _vm.ActiveTab.FullPath, extended: false);
        }
        catch
        {
            _preparedShellMenu = null;   // it will simply be built on demand
        }
    }

    void DiscardPreparedShellMenu()
    {
        _preparedShellMenu?.Dispose();
        _preparedShellMenu = null;
    }

    ShellContextMenu.PreparedMenu? TakePreparedShellMenu(IReadOnlyList<string> paths, bool background, bool extended)
    {
        var prepared = _preparedShellMenu;
        if (prepared == null || !prepared.Matches(paths, background, extended))
            return null;
        _preparedShellMenu = null;
        return prepared;
    }

    void FileListMenu_Opened(object sender, RoutedEventArgs e)
    {
        var paths = SelectedRows.Select(i => i.FullPath).ToList();
        // After the menu has painted, so it appears without waiting for this.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (FileList.ContextMenu.IsOpen)
                PrepareShellMenu(paths);
        });
    }

    void ContextMenu_Closed(object sender, RoutedEventArgs e) =>
        // Lower priority than the Windows-menu open request, so a menu that is about to be used survives.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, DiscardPreparedShellMenu);

    void ShowWindowsMenu(IReadOnlyList<string> paths, Point screenPoint, bool extended)
    {
        try
        {
            if (TakePreparedShellMenu(paths, background: false, extended) is { } prepared)
                ShellContextMenu.Show(this, prepared, screenPoint, _vm.HandleShellVerb);
            else
                ShellContextMenu.ShowForItems(this, paths, screenPoint, extended, _vm.HandleShellVerb);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "탐색기 메뉴를 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowWindowsBackgroundMenu(Point screenPoint, bool extended)
    {
        if (_vm.InVirtual)
            return;   // 내 PC and result lists are not folders; they have no background menu of their own
        try
        {
            if (TakePreparedShellMenu(new[] { _vm.ActiveTab.FullPath }, background: true, extended) is { } prepared)
                ShellContextMenu.Show(this, prepared, screenPoint, null);
            else
                ShellContextMenu.ShowForFolderBackground(this, _vm.ActiveTab.FullPath, screenPoint, extended);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "탐색기 메뉴를 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
