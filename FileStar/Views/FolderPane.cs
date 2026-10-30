using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FileStar.Views;

/// <summary>A column of the main list, so the second pane can show exactly the same ones.</summary>
public sealed record PaneColumn(string Key, string Title, double Width, DataTemplate? CellTemplate, BindingBase? Binding);

/// <summary>
/// 두 창 보기: the right-hand folder list, the view of <see cref="FolderPaneViewModel"/> (its DataContext). It mirrors
/// the main list (same columns, same sort, same memo markers) and has only a title strip of its own.
/// </summary>
public sealed class FolderPane : DockPanel
{
    /// <summary>Height of the title strip above each list in 두 창 보기 (the left list's strip uses it too).</summary>
    public const double BarHeight = 30;

    readonly Border _bar = new() { Height = BarHeight, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Brushes.Transparent };
    readonly ListView _list = new() { BorderThickness = new Thickness(0), AllowDrop = true };
    FolderPaneViewModel? _vm;
    Point? _dragStart;

    public FolderPane()
    {
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");
        BuildTitleStrip();

        // ── List (columns come from the main list via SyncColumns) ──
        _list.View = new GridView();
        _list.SelectionMode = SelectionMode.Extended;
        _list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("List.View"));
        Children.Add(_list);

        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem)
                _vm?.OpenSelected();
        };
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.SelectionChanged += (_, _) => _vm?.List.SetSelection(_list.SelectedItems.Cast<FileItem>().ToList());
        _list.PreviewMouseLeftButtonDown += (_, e) =>
            _dragStart = ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem ? e.GetPosition(_list) : null;
        _list.PreviewMouseMove += List_PreviewMouseMove;
        _list.DragOver += List_DragOver;
        _list.DragEnter += List_DragOver;
        _list.Drop += List_Drop;
        _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is GridViewColumnHeader { Tag: string key })
                HeaderClicked?.Invoke(key);
        }));
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, _) =>
        {
            if (_vm != null)
                MenuBuilder.Fill(_list.ContextMenu.Items, _vm.ContextMenu());
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as FolderPaneViewModel);
    }

    /// <summary>A column header was clicked; the window sorts both lists the same way.</summary>
    public event Action<string>? HeaderClicked;

    /// <summary>Keys the main window would otherwise take for itself (it checks this first).</summary>
    public bool HasFocus => _list.IsKeyboardFocusWithin;

    void BuildTitleStrip()
    {
        _bar.SetResourceReference(Border.BackgroundProperty, "Th.Toolbar.Bg");
        _bar.SetBinding(ToolTipProperty, new Binding(nameof(FolderPaneViewModel.Folder)));
        var strip = new DockPanel { Margin = new Thickness(8, 0, 2, 0) };
        var icon = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Th.Folder");
        var close = new Button { Content = "", ToolTip = "두 창 보기 닫기 (Ctrl+Shift+D)", Width = 26, Height = 24, Padding = new Thickness(0) };
        close.SetResourceReference(StyleProperty, "ToolButton");
        close.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(FolderPaneViewModel.CloseCommand)));
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderPaneViewModel.Title)));
        SetDock(icon, Dock.Left);
        SetDock(close, Dock.Right);
        strip.Children.Add(icon);
        strip.Children.Add(close);
        strip.Children.Add(title);
        _bar.Child = strip;
        _bar.MouseLeftButtonDown += (_, _) => _list.Focus();
        SetDock(_bar, Dock.Top);
        Children.Add(_bar);
    }

    void Attach(FolderPaneViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.List.SelectionRequested -= SelectRows;
            _vm.List.ScrollToTopRequested -= ScrollToTop;
        }
        _vm = vm;
        if (vm == null)
            return;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.List.SelectionRequested += SelectRows;
        vm.List.ScrollToTopRequested += ScrollToTop;
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderPaneViewModel.IsActive))
            return;
        // The accent line under the title strip marks the list that has the keyboard.
        if (_vm!.IsActive)
            _bar.SetResourceReference(Border.BorderBrushProperty, "Th.Accent");
        else
            _bar.BorderBrush = Brushes.Transparent;
    }

    void SelectRows(IReadOnlyList<FileItem> items)
    {
        _list.SelectedItems.Clear();
        foreach (var item in items)
            _list.SelectedItems.Add(item);
        if (items.FirstOrDefault() is FileItem first)
            _list.ScrollIntoView(first);
    }

    void ScrollToTop()
    {
        if (_list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[0]);
    }

    public void SyncColumns(IEnumerable<PaneColumn> columns)
    {
        var grid = (GridView)_list.View;
        grid.Columns.Clear();
        foreach (var c in columns)
        {
            var column = new GridViewColumn
            {
                Header = new GridViewColumnHeader { Tag = c.Key, Content = c.Title },
                Width = c.Width,
            };
            if (c.CellTemplate != null)
                column.CellTemplate = c.CellTemplate;
            else
                column.DisplayMemberBinding = c.Binding;
            grid.Columns.Add(column);
        }
    }

    // ── Keyboard: list keys the window leaves to the pane while it has focus ──

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control;
        if (none && key == Key.Enter) _vm.OpenSelected();
        else if (none && key == Key.Back) _vm.GoBack();
        else if (mods == ModifierKeys.Alt && key == Key.Up) _vm.GoUp();
        else if (none && key == Key.F5) _ = _vm.ReloadAsync();
        else if (ctrl && key == Key.C) _vm.SetClipboard(cut: false);
        else if (ctrl && key == Key.X) _vm.SetClipboard(cut: true);
        else if (ctrl && key == Key.V) _ = _vm.PasteAsync();
        else if (ctrl && key == Key.A) _list.SelectAll();
        else if (key == Key.Delete && (none || mods == ModifierKeys.Shift)) _ = _vm.DeleteAsync(permanently: mods == ModifierKeys.Shift);
        else if (none && key == Key.F2) _ = _vm.RenameAsync();
        else
            return;
        e.Handled = true;
    }

    // ── Drag and drop (same rules as the main list) ──

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var position = e.GetPosition(_list);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragStart = null;
        var paths = _list.SelectedItems.Cast<FileItem>().Select(i => i.FullPath).ToArray();
        if (paths.Length == 0)
            return;
        try
        {
            DragDrop.DoDragDrop(_list, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
    }

    string? DropFolder(DragEventArgs e) =>
        e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListViewItem { DataContext: FileItem { IsDirectory: true } row }
            ? row.FullPath
            : _vm is { HasFolder: true } vm ? vm.Folder : null;

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DropRules.DraggedFiles(e) is { } files && DropFolder(e) is string folder
            ? DropRules.PreviewEffect(e, files, folder)
            : DragDropEffects.None;
    }

    async void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm == null || DropRules.DraggedFiles(e) is not { } files || DropFolder(e) is not string folder || DropRules.IsInsideDragged(files, folder))
            return;
        var effect = DropRules.EffectFor(e, files, folder);
        if (effect != DragDropEffects.None)
            await _vm.ReceiveAsync(files, move: effect == DragDropEffects.Move, folder);
    }
}
