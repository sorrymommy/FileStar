using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStar.Views;

// "탐색기 메뉴": Windows Explorer's own right-click menu for the selection, a tree folder, or the folder background.
public partial class MainWindow
{
    readonly DispatcherTimer _windowsMenuHover = new() { Interval = TimeSpan.FromMilliseconds(200) };
    ShellMenuThread? _shellMenus;
    (ShellMenuRequest For, Task<ShellContextMenu.PreparedMenu> Menu)? _preparedShellMenu;

    /// <summary>Which Windows menu: for items, or for the background of the folder <c>Paths[0]</c>.</summary>
    sealed record ShellMenuRequest(IReadOnlyList<string> Paths, bool Background, bool Extended)
    {
        public bool SameAs(ShellMenuRequest other) =>
            Background == other.Background && Extended == other.Extended &&
            Paths.SequenceEqual(other.Paths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Started with the window's first Windows menu (the start-up prewarm) and ended with the window.</summary>
    ShellMenuThread ShellMenus
    {
        get
        {
            if (_shellMenus == null)
            {
                var thread = _shellMenus = new ShellMenuThread();
                Closed += (_, _) => thread.Dispose();
            }
            return _shellMenus;
        }
    }

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
    // Building it is the slow part (0.4–2 s: every installed extension adds items). While FileStar's own menu is
    // open, the user still has to move to "탐색기 메뉴", so it is built then, on the shell menu thread so FileStar's
    // menu keeps responding, and shown without waiting on arrival.

    void PrepareShellMenu(IReadOnlyList<string>? paths)
    {
        DiscardPreparedShellMenu();
        if (paths is not { Count: > 0 } && _vm.InVirtual)
            return;
        var request = paths is { Count: > 0 }
            ? new ShellMenuRequest(paths, Background: false, Extended: false)
            : new ShellMenuRequest(new[] { _vm.ActiveTab.FullPath }, Background: true, Extended: false);
        _preparedShellMenu = (request, ShellMenus.PrepareAsync(WindowHandle, request.Paths, request.Background, request.Extended));
    }

    void DiscardPreparedShellMenu()
    {
        if (_preparedShellMenu is { } prepared)
            ShellMenus.Release(prepared.Menu);
        _preparedShellMenu = null;
    }

    Task<ShellContextMenu.PreparedMenu>? TakePreparedShellMenu(ShellMenuRequest request)
    {
        if (_preparedShellMenu is not { } prepared || !prepared.For.SameAs(request))
            return null;
        _preparedShellMenu = null;
        return prepared.Menu;
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

    void ShowWindowsMenu(IReadOnlyList<string> paths, Point screenPoint, bool extended) =>
        _ = ShowShellMenuAsync(new ShellMenuRequest(paths, Background: false, extended), screenPoint, _vm.HandleShellVerb);

    void ShowWindowsBackgroundMenu(Point screenPoint, bool extended)
    {
        if (_vm.InVirtual)
            return;   // 내 PC and result lists are not folders; they have no background menu of their own
        _ = ShowShellMenuAsync(new ShellMenuRequest(new[] { _vm.ActiveTab.FullPath }, Background: true, extended), screenPoint, null);
    }

    IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    async Task ShowShellMenuAsync(ShellMenuRequest request, Point screenPoint, ShellContextMenu.VerbHandler? handleVerb)
    {
        try
        {
            var menu = TakePreparedShellMenu(request) ?? ShellMenus.PrepareAsync(WindowHandle, request.Paths, request.Background, request.Extended);
            var shown = ShellMenus.ShowAsync(menu, WindowHandle, screenPoint, handleVerb);
            if (!menu.IsCompleted)
                await ShowBusyUntil(menu);
            await shown;
        }
        catch (OperationCanceledException)
        {
            // The window closed while the menu was being built.
        }
        catch (Exception ex)
        {
            Perf.Log("shell menu: " + ex);
            if (IsLoaded)
                MessageBox.Show(this, "탐색기 메뉴를 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The "working in the background" pointer while a menu is still being built on demand.</summary>
    async Task ShowBusyUntil(Task building)
    {
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            await Task.WhenAny(building);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
