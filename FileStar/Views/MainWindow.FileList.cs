using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStar.Views;

// The file list's view side: keeping the ViewModel's selection in step, the selection rectangle, dragging
// files out and dropping them in, the in-place rename box, and the right-click menu's dynamic parts.
public partial class MainWindow
{
    Point? _marqueeStart, _dragStart;
    bool _marqueeActive, _marqueeFromBlank;
    HashSet<FileItem> _marqueeBase = new();
    FileItem? _dragNarrowTo;

    List<FileItem> SelectedRows => FileList.SelectedItems.Cast<FileItem>().ToList();

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => _vm.FileList.SetSelection(SelectedRows);

    /// <summary>The ViewModel asked for these rows to be selected (after a reload, a rename, a delete …).</summary>
    void SelectRows(IReadOnlyList<FileItem> items)
    {
        FileList.SelectedItems.Clear();
        foreach (var item in items)
            FileList.SelectedItems.Add(item);
        if (items.FirstOrDefault() is not FileItem first)
            return;
        FileList.ScrollIntoView(first);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FileList.ItemContainerGenerator.ContainerFromItem(first) is ListViewItem container && FileList.IsKeyboardFocusWithin)
                container.Focus();
        });
    }

    void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on column headers or empty space.
        if (e.ChangedButton == MouseButton.Left &&
            ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem)
            _vm.OpenSelected();
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;   // typing in the rename box, not commands for the list
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.F2 && mods == ModifierKeys.None) _vm.RenameSelected();
        else if (key == Key.Enter && mods == ModifierKeys.Alt) _vm.ShowSelectedProperties();
        else if (key == Key.Enter && mods == ModifierKeys.None) _vm.OpenSelected();
        else if (key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift)) _vm.CopySelectedPaths();
        else
            return;
        e.Handled = true;
    }

    void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Shift+right-click: straight to the Windows menu (with extended items). On empty space it is the
        // folder's background menu (새로 만들기, 붙여넣기 …), as in Explorer.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            bool onItem = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FileList, source) is ListViewItem;
            var point = ShellContextMenu.CursorPosition();
            if (onItem && FileList.SelectedItems.Count > 0)
                ShowWindowsMenu(SelectedRows.Select(i => i.FullPath).ToList(), point, extended: true);
            else
                ShowWindowsBackgroundMenu(point, extended: true);
            return;
        }
        _vm.RefreshFileMenu();
        MenuBuilder.Apply(FileFavoriteMenuItem, _vm.FileFavoriteMenuItem());
    }

    // ── In-place rename (F2) ──

    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || box.DataContext is not FileItem item)
            return;
        box.Text = item.Name;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            // Like Explorer: select the name without its extension.
            box.Select(0, FileNames.StemLength(item.Name, item.RawExtension, item.IsDirectory));
        });
    }

    void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItem item } box)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.CommitRename(item, box.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CancelRename(item);
        }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.None ||
                 e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            // Rename file after file: ↑/Shift+Tab saves and edits the previous name, ↓/Tab the next one.
            e.Handled = true;
            bool previous = e.Key == Key.Up || e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift;
            _vm.CommitRename(item, box.Text, next: previous ? -1 : 1);
        }
    }

    void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere saves, as in Explorer.
        if (sender is TextBox { DataContext: FileItem item } box && item.IsRenaming)
            _vm.CommitRename(item, box.Text);
    }

    // ── Drag-rectangle (rubber band) selection ──
    // Starts from empty space, or from a row anywhere except the file's icon/name (dragging there keeps the
    // list's normal behaviour). Every row the rectangle touches is selected; Ctrl adds to the existing selection.

    void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _marqueeStart = null;
        var source = e.OriginalSource as DependencyObject;
        for (var node = source; node != null && node != FileList; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            // Scroll bars, column headers and the rename box keep their own mouse handling.
            if (node is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader or TextBox)
                return;
            // The icon and the name text: drag the files out (to Explorer, the desktop, a folder here …).
            if (node is FrameworkElement { Name: "NameText" } or Image)
            {
                BeginFileDrag(e, source!);
                return;
            }
        }
        _marqueeStart = e.GetPosition(FileList);
        _marqueeFromBlank = ItemsControl.ContainerFromElement(FileList, source!) is not ListViewItem;
    }

    void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart != null)
        {
            ContinueFileDrag(e);
            return;
        }
        if (_marqueeStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var position = e.GetPosition(FileList);
        if (!_marqueeActive)
        {
            if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _marqueeActive = true;
            _vm.DeferSelectionUpdates = true;   // dragging a selection rectangle: the panes catch up when it ends
            _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectedRows.ToHashSet() : new();
            FileList.CaptureMouse();
            MarqueeRect.Visibility = Visibility.Visible;
        }

        var rect = new Rect(start, position);
        rect.Intersect(new Rect(0, 0, FileList.ActualWidth, FileList.ActualHeight));
        if (rect.IsEmpty)
            rect = new Rect(position, position);
        Canvas.SetLeft(MarqueeRect, rect.X);
        Canvas.SetTop(MarqueeRect, rect.Y);
        MarqueeRect.Width = rect.Width;
        MarqueeRect.Height = rect.Height;
        UpdateMarqueeSelection(rect);
        e.Handled = true;
    }

    void UpdateMarqueeSelection(Rect rect)
    {
        foreach (var item in _vm.FileList.VisibleItems)
        {
            // Only rows on screen have containers; others keep their current state.
            if (FileList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container || !container.IsVisible)
                continue;
            var bounds = container.TransformToAncestor(FileList).TransformBounds(new Rect(container.RenderSize));
            bool select = bounds.IntersectsWith(rect) || _marqueeBase.Contains(item);
            if (select != container.IsSelected)
                container.IsSelected = select;
        }
    }

    void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart != null)
        {
            // A click (no drag) on one of several selected rows: now narrow the selection to it, as in Explorer.
            if (_dragNarrowTo is FileItem only)
            {
                FileList.SelectedItems.Clear();
                FileList.SelectedItem = only;
            }
            _dragStart = null;
            _dragNarrowTo = null;
        }
        if (_marqueeActive)
        {
            EndMarquee();
            e.Handled = true;
        }
        else if (_marqueeStart != null && _marqueeFromBlank && Keyboard.Modifiers == ModifierKeys.None)
        {
            FileList.UnselectAll();   // click on empty space clears the selection, as in Explorer
        }
        _marqueeStart = null;
    }

    void FileList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
            EndMarquee();
    }

    void EndMarquee()
    {
        _marqueeActive = false;
        _marqueeStart = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (FileList.IsMouseCaptured)
            FileList.ReleaseMouseCapture();
        // Selection changes were kept light while dragging; refresh the panes once now.
        _vm.DeferSelectionUpdates = false;
        _vm.OnSelectionSettled();
    }

    // ── Drag and drop ──
    // Out: the selected files go to any program that takes files (Explorer, the desktop, mail …); that program
    // decides between copy and move, as with Explorer. In: files dropped on the list, on a folder row or on a
    // tree folder are copied or moved there, so memos and tags come along.

    void BeginFileDrag(MouseButtonEventArgs e, DependencyObject source)
    {
        if (ItemsControl.ContainerFromElement(FileList, source) is not ListViewItem { DataContext: FileItem item } container)
            return;
        _dragStart = e.GetPosition(FileList);
        _dragNarrowTo = null;
        // Pressing on one of several selected rows must keep them all selected, or they couldn't be dragged
        // together; the selection narrows on release instead (FileList_PreviewMouseLeftButtonUp).
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && container.IsSelected && FileList.SelectedItems.Count > 1)
        {
            _dragNarrowTo = item;
            container.Focus();
            e.Handled = true;
        }
    }

    void ContinueFileDrag(MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStart = null;
            return;
        }
        var position = e.GetPosition(FileList);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;
        _dragNarrowTo = null;
        var paths = SelectedRows.Select(i => i.FullPath).ToArray();
        if (paths.Length == 0)
            return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        // Drives and user folders listed in 내 PC can be copied or linked, never moved away.
        var allowed = _vm.InThisPc ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        try
        {
            DragDrop.DoDragDrop(FileList, data, allowed);
        }
        catch { }
    }

    /// <summary>The folder a drop at this point goes into: a folder row or tree folder under the mouse, else the current folder.</summary>
    string? DropFolderAt(DragEventArgs e, object sender)
    {
        var source = e.OriginalSource as DependencyObject;
        if (sender == FolderTree)
            return source.FindAncestor<TreeViewItem>() is { DataContext: FolderNode { IsFolder: true } folder } ? folder.FullPath : null;
        if (source != null && ItemsControl.ContainerFromElement(FileList, source) is ListViewItem { DataContext: FileItem { IsDirectory: true } row })
            return row.FullPath;
        return _vm.InVirtual ? null : _vm.ActiveTab.FullPath;
    }

    void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DropRules.DraggedFiles(e) is { } files && DropFolderAt(e, sender) is string folder
            ? DropRules.PreviewEffect(e, files, folder)
            : DragDropEffects.None;
    }

    async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DropRules.DraggedFiles(e) is not { } files || DropFolderAt(e, sender) is not string folder || DropRules.IsInsideDragged(files, folder))
            return;
        var effect = DropRules.EffectFor(e, files, folder);
        if (effect == DragDropEffects.None)
            return;
        e.Effects = effect;
        await _vm.DropAsync(files, folder, move: effect == DragDropEffects.Move);
    }
}
