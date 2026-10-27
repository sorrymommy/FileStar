using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStar.Views;

// The folder tree's view side: scrolling to the current folder (the tree is virtualized), keeping it from
// shaking sideways, the right-click menu, and the in-place rename box. What the tree shows is FolderTreeViewModel.
public partial class MainWindow
{
    FolderNode? _contextNode;
    bool _bringingTreeRow;
    ScrollViewer? _treeScroller;
    double _treeHorizontalOffset;
    DateTime _treeWheelAt;

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node)
            _vm.Tree.OnSelected(node);
    }

    /// <summary>Realizes each container along the chain (the tree is virtualized) and scrolls the last one into view.</summary>
    void BringTreeChainIntoView(IReadOnlyList<FolderNode> chain)
    {
        ItemsControl parent = FolderTree;
        TreeViewItem? container = null;
        foreach (var node in chain)
        {
            container = GetTreeContainer(parent, node);
            if (container == null)
                return;
            parent = container;
        }
        if (container == null)
            return;
        container.BringIntoView();
        // Newly expanded branches may still be measuring; scroll again once layout has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            var path = _vm.Tree.PathChain;
            if (path.Count > 0 && container.DataContext == path[^1])
                container.BringIntoView();   // vertical only (TreeItem_RequestBringIntoView, TreeScroller_ScrollChanged)
        });
    }

    static TreeViewItem? GetTreeContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem existing)
            return existing;

        parent.ApplyTemplate();
        var presenter = parent.FindVisualChild<ItemsPresenter>();
        if (presenter == null)
        {
            parent.UpdateLayout();
            presenter = parent.FindVisualChild<ItemsPresenter>();
        }
        if (presenter == null)
            return null;

        presenter.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0)
            parent.UpdateLayout();
        if (VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingStackPanel panel)
        {
            int index = parent.Items.IndexOf(item);
            if (index >= 0)
                panel.BringIndexIntoViewPublic(index);
        }
        return parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
    }

    void TreeItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Tunnels outer → inner, so the innermost (clicked) item is assigned last.
        if (sender is TreeViewItem { DataContext: FolderNode node })
            _contextNode = node;
    }

    /// <summary>
    /// Selecting a folder whose name is wider than the tree made WPF scroll sideways to show all of it, and
    /// <see cref="BringTreeChainIntoView"/> then scrolled back: the tree shook left and right. Scroll only up
    /// or down to the item's own row (not its expanded children), keeping where the tree is sideways.
    /// </summary>
    void TreeItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // The nearest item handles it, whether the item itself or something in its row (its text, the rename box) asked.
        if (sender is not TreeViewItem item || _bringingTreeRow)
            return;
        e.Handled = true;
        var header = item.Template?.FindName("PART_Header", item) as FrameworkElement;
        double top = header?.TranslatePoint(new Point(0, 0), item).Y ?? 0;
        double height = header?.ActualHeight is > 0 and var h ? h : 22;
        // A 1-pixel-wide target at the left edge of the view: nothing to scroll sideways for.
        double x = _treeScroller != null ? _treeScroller.TranslatePoint(new Point(0, 0), item).X : 0;
        _bringingTreeRow = true;
        try
        {
            item.BringIntoView(new Rect(Math.Max(0, x), top, 1, height));
        }
        finally
        {
            _bringingTreeRow = false;
        }
    }

    // The tree only scrolls sideways when the user does it (its scrollbar, the mouse wheel). Expanding a
    // favorite or selecting a folder with a long name used to scroll it right and then back, so it shook.

    void FolderTree_Loaded(object sender, RoutedEventArgs e)
    {
        if (_treeScroller != null)
            return;
        _treeScroller = FolderTree.FindVisualChild<ScrollViewer>();
        if (_treeScroller != null)
            _treeScroller.ScrollChanged += TreeScroller_ScrollChanged;
    }

    void FolderTree_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => _treeWheelAt = DateTime.Now;

    void TreeScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        if (e.HorizontalChange == 0 && e.ExtentWidthChange == 0 && e.ViewportWidthChange == 0)
            return;
        var bar = scroller.Template.FindName("PART_HorizontalScrollBar", scroller) as ScrollBar;
        bool byUser = bar is { IsMouseOver: true } || bar is { IsMouseCaptureWithin: true } ||
                      (DateTime.Now - _treeWheelAt).TotalMilliseconds < 300;
        double keep = Math.Min(_treeHorizontalOffset, Math.Max(0, scroller.ExtentWidth - scroller.ViewportWidth));
        if (byUser || Math.Abs(scroller.HorizontalOffset - keep) < 0.5)
            _treeHorizontalOffset = scroller.HorizontalOffset;
        else
            scroller.ScrollToHorizontalOffset(keep);   // undone in the same layout pass, before it is drawn
    }

    void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => _contextNode = null;

    void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Opened from the keyboard (Shift+F10, menu key) → act on the selected node.
        var node = e.CursorLeft < 0 ? _vm.Tree.Selected : _contextNode;
        // Shift+right-click: straight to the Windows menu (with extended items), as in Explorer.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && node is { IsFolder: true })
        {
            e.Handled = true;
            ShowWindowsMenu(new[] { node.FullPath! }, ShellContextMenu.CursorPosition(), extended: true);
            return;
        }
        var menu = FolderTree.ContextMenu;
        MenuBuilder.Fill(menu.Items, _vm.TreeContextMenu(node));
        if (node is { IsFolder: true })
        {
            string path = node.FullPath!;
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateWindowsMenuItem(point => ShowWindowsMenu(new[] { path }, point, extended: false)));
        }
    }

    void FolderTreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var node = _contextNode ?? _vm.Tree.Selected;
        if (node is not { IsFolder: true })
            return;
        var paths = new[] { node.FullPath! };
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (FolderTree.ContextMenu.IsOpen)
                PrepareShellMenu(paths);
        });
    }

    // ── In-place rename (F2) ──

    void TreeRenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || box.DataContext is not FolderNode node)
            return;
        box.Text = Path.GetFileName(node.FullPath!);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    void TreeRenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FolderNode node } box)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.CommitTreeRename(node, box.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CancelTreeRename(node);
        }
    }

    void TreeRenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere saves, as in Explorer.
        if (sender is TextBox { DataContext: FolderNode node } box && node.IsRenaming)
            _vm.CommitTreeRename(node, box.Text);
    }
}
