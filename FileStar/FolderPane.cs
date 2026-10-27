using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace FileStar;

/// <summary>What the second pane needs from the main window.</summary>
public interface IFolderPaneHost
{
    IntPtr WindowHandle { get; }
    /// <summary>The folder the main list shows, or null when it shows 내 PC or a result list.</summary>
    string? MainFolder { get; }
    void ShellOpen(string path);
    void Close(FolderPane pane);
    /// <summary>Files changed through the pane: the main list may show them too.</summary>
    void FilesChanged();
}

/// <summary>A column of the main list, so the second pane can show exactly the same ones.</summary>
public sealed record PaneColumn(string Key, string Title, double Width, DataTemplate? CellTemplate, BindingBase? Binding);

/// <summary>
/// 두 창 보기: the right-hand folder list. It mirrors the main list (same columns, same sort, same memo markers)
/// and has only a title strip of its own; the window's address bar, back/forward/up, tree, filter and info
/// pane serve whichever list is active. File operations go through FileOps, so memos follow and Ctrl+Z works.
/// </summary>
public sealed class FolderPane : DockPanel
{
    /// <summary>Height of the title strip above each list in 두 창 보기 (the left list's strip uses it too).</summary>
    public const double BarHeight = 30;

    readonly Border _bar = new() { Height = BarHeight, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = System.Windows.Media.Brushes.Transparent };
    readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ListView _list = new() { BorderThickness = new Thickness(0), AllowDrop = true, AlternationCount = int.MaxValue };
    readonly Stack<string> _back = new(), _forward = new();
    readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    FileSystemWatcher? _watcher;
    ListCollectionView? _view;
    Point? _dragStart;
    string _sortKey = "Name";
    bool _sortDescending;
    Predicate<object>? _filter;

    public IFolderPaneHost? Host { get; set; }

    /// <summary>The folder shown (empty until <see cref="Navigate"/>).</summary>
    public string Folder { get; private set; } = "";

    /// <summary>The folder changed (the window updates its address bar, tree and info pane).</summary>
    public event Action? Navigated;
    public event Action? SelectionChanged;
    /// <summary>The list was (re)loaded: compare results need recomputing.</summary>
    public event Action? Reloaded;
    /// <summary>A column header was clicked; the window sorts both lists the same way.</summary>
    public event Action<string>? HeaderClicked;

    public FolderPane()
    {
        SetResourceReference(BackgroundProperty, "Th.Window.Bg");

        // ── Title strip: folder name and close, the same height and look as the left list's ──
        _bar.SetResourceReference(Border.BackgroundProperty, "Th.Toolbar.Bg");
        var strip = new DockPanel { Margin = new Thickness(8, 0, 2, 0) };
        var icon = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Th.Folder");
        var close = new Button { Content = "", ToolTip = "두 창 보기 닫기 (Ctrl+Shift+D)", Width = 26, Height = 24, Padding = new Thickness(0) };
        close.SetResourceReference(StyleProperty, "ToolButton");
        close.Click += (_, _) => Host?.Close(this);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        strip.Children.Add(icon);
        strip.Children.Add(close);
        strip.Children.Add(_title);
        _bar.Child = strip;
        _bar.MouseLeftButtonDown += (_, _) => _list.Focus();
        DockPanel.SetDock(_bar, Dock.Top);
        Children.Add(_bar);

        // ── List (columns come from the main list via SyncColumns) ──
        _list.View = new GridView();
        _list.SelectionMode = SelectionMode.Extended;
        Children.Add(_list);

        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem)
                OpenSelected();
        };
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
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
        _list.ContextMenuOpening += (_, _) => FillMenu(_list.ContextMenu);

        _watchTimer.Tick += (_, _) =>
        {
            _watchTimer.Stop();
            Reload();
        };
        NotesStore.Changed += () => Dispatcher.BeginInvoke(RefreshNotes);
    }

    public List<FileItem> Selected => _list.SelectedItems.Cast<FileItem>().ToList();

    /// <summary>Every item in the folder (ignoring the filter), for comparing with the other list.</summary>
    public IEnumerable<FileItem> AllItems => _view?.SourceCollection.Cast<FileItem>() ?? [];

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    // ── Columns, sort, filter: driven by the window so both lists match ──

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

    public void SetSort(string key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        if (_view != null)
            _view.CustomSort = new FileItemComparer(_sortKey, _sortDescending);
    }

    public void SetFilter(Predicate<object>? filter)
    {
        _filter = filter;
        if (_view != null)
        {
            _view.Filter = _filter;
            _view.Refresh();
        }
    }

    /// <summary>Redraws after compare marks changed.</summary>
    public void RefreshView() => _view?.Refresh();

    // ── Navigation (the window's back/forward/up and address bar call these while this pane is active) ──

    public void Navigate(string path, string? selectName = null)
    {
        try
        {
            path = FileSystem.Normalize(path);
        }
        catch
        {
            return;
        }
        if (!Directory.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, $"'{path}' 폴더를 찾을 수 없습니다.", AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(Folder, path, StringComparison.OrdinalIgnoreCase))
        {
            if (selectName != null)
                Select(selectName);
            return;
        }
        if (Folder.Length > 0)
            _back.Push(Folder);
        _forward.Clear();
        Show(path, selectName);
    }

    public void GoBack()
    {
        if (_back.Count == 0)
            return;
        string previous = Folder;
        _forward.Push(previous);
        Show(_back.Pop(), Path.GetFileName(previous));
    }

    public void GoForward()
    {
        if (_forward.Count == 0)
            return;
        _back.Push(Folder);
        Show(_forward.Pop());
    }

    public void GoUp()
    {
        if (Directory.GetParent(Folder) is { } parent)
            Navigate(parent.FullName, Path.GetFileName(Folder));
    }

    void Show(string path, string? selectName = null)
    {
        Folder = path;
        _title.Text = FileSystem.DisplayName(path);
        _bar.ToolTip = path;
        Watch();
        Reload(selectName == null ? null : new[] { selectName });
        Navigated?.Invoke();
    }

    void OpenSelected()
    {
        var items = Selected;
        if (items is [{ IsDirectory: true } folder])
            Navigate(folder.FullPath);
        else
            foreach (var item in items.Where(i => !i.IsDirectory))
                Host?.ShellOpen(item.FullPath);
    }

    /// <summary>Reloads the folder, keeping the selection.</summary>
    public void Reload(IEnumerable<string>? select = null)
    {
        if (Folder.Length == 0)
            return;
        var keep = (select ?? Selected.Select(i => i.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<FileItem> items;
        try
        {
            items = FileSystem.GetEntries(Folder).Select(FileItem.From).OfType<FileItem>().ToList();
        }
        catch
        {
            _list.ItemsSource = null;
            return;
        }
        foreach (var item in items)
        {
            item.Note = NotesStore.Get(item.FullPath);
            if (item.IsDirectory)
                item.ContainsNotes = NotesStore.FolderHasNotes(item.FullPath);
        }
        _view = new ListCollectionView(items) { CustomSort = new FileItemComparer(_sortKey, _sortDescending), Filter = _filter };
        _list.ItemsSource = _view;
        foreach (var item in items.Where(i => keep.Contains(i.Name)))
            _list.SelectedItems.Add(item);
        if (_list.SelectedItem is FileItem first)
            _list.ScrollIntoView(first);
        Reloaded?.Invoke();
    }

    void Select(string name)
    {
        if (_view?.Cast<FileItem>().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is FileItem item)
        {
            _list.SelectedItem = item;
            _list.ScrollIntoView(item);
        }
    }

    void RefreshNotes()
    {
        foreach (var row in AllItems)
        {
            row.Note = NotesStore.Get(row.FullPath);
            if (row.IsDirectory)
                row.ContainsNotes = NotesStore.FolderHasNotes(row.FullPath);
        }
    }

    void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            void OnChange(object? s, EventArgs e) => Dispatcher.BeginInvoke(() =>
            {
                if (s != _watcher)
                    return;
                _watchTimer.Stop();
                _watchTimer.Start();
            });
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Changed += OnChange;
            watcher.Renamed += OnChange;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch { }
    }

    // ── Keyboard: list keys the window leaves to the pane while it has focus ──

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control;
        if (none && key == Key.Enter) OpenSelected();
        else if (none && key == Key.Back) GoBack();
        else if (mods == ModifierKeys.Alt && key == Key.Up) GoUp();
        else if (none && key == Key.F5) Reload();
        else if (ctrl && key == Key.C) SetClipboard(cut: false);
        else if (ctrl && key == Key.X) SetClipboard(cut: true);
        else if (ctrl && key == Key.V) Paste();
        else if (ctrl && key == Key.A) _list.SelectAll();
        else if (key == Key.Delete && (none || mods == ModifierKeys.Shift)) Delete(permanently: mods == ModifierKeys.Shift);
        else if (none && key == Key.F2) Rename();
        else
            return;
        e.Handled = true;
    }

    /// <summary>Keys the main window would otherwise take for itself (it checks this first).</summary>
    public bool HasFocus => _list.IsKeyboardFocusWithin;

    public void FocusList() => _list.Focus();

    /// <summary>The accent line under the title strip marks the list that has the keyboard.</summary>
    public void SetActive(bool active)
    {
        if (active)
            _bar.SetResourceReference(Border.BorderBrushProperty, "Th.Accent");
        else
            _bar.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    // ── File operations (through FileOps: Recycle Bin, conflicts, memos, undo) ──

    void SetClipboard(bool cut)
    {
        var paths = Selected.Select(i => i.FullPath).ToList();
        if (paths.Count > 0)
            FileOps.SetClipboard(paths, cut);
    }

    async void Paste()
    {
        if (Folder.Length == 0 || Host == null)
            return;
        if (await FileOps.PasteAsync(Host.WindowHandle, Folder))
            AfterChange();
    }

    async void Delete(bool permanently)
    {
        var paths = Selected.Select(i => i.FullPath).ToList();
        if (paths.Count == 0 || Host == null)
            return;
        if (await FileOps.DeleteAsync(Host.WindowHandle, paths, permanently))
            AfterChange();
    }

    void Rename()
    {
        if (Selected is not [var item])
            return;
        if (InputDialog.Ask(Window.GetWindow(this)!, "이름 바꾸기", "새 이름:", item.Name) is not string newName || newName == item.Name)
            return;
        string target = Path.Combine(Folder, newName);
        try
        {
            if (item.IsDirectory)
                Directory.Move(item.FullPath, target);
            else
                File.Move(item.FullPath, target);
            NotesStore.Rename(item.FullPath, target);
            UndoStack.Push(new RenameUndo(item.FullPath, target));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "이름을 바꿀 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Reload(new[] { newName });
        Host?.FilesChanged();
    }

    /// <summary>Asks for a name (suggesting "새 폴더") and creates the folder here.</summary>
    public void NewFolder()
    {
        if (Folder.Length == 0 || FileSystem.IsVirtual(Folder))
            return;
        if (InputDialog.Ask(Window.GetWindow(this)!, "새 폴더", "폴더 이름:", FileSystem.NewFolderName(Folder)) is not string name || name.Trim().Length == 0)
            return;
        name = name.Trim();
        string target = Path.Combine(Folder, name);
        try
        {
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException($"'{name}'이(가) 이미 있습니다.");
            Directory.CreateDirectory(target);
            UndoStack.Push(new CreateUndo(new[] { target }, "새 폴더"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "새 폴더를 만들 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Reload(new[] { name });
        Host?.FilesChanged();
    }

    /// <summary>Copies or moves the selection into the main list's folder.</summary>
    async void SendToMain(bool move)
    {
        var paths = Selected.Select(i => i.FullPath).ToList();
        if (paths.Count == 0 || Host?.MainFolder is not string target)
            return;
        if (await FileOps.TransferAsync(Host.WindowHandle, paths, target, move))
            AfterChange();
    }

    void AfterChange()
    {
        Reload();
        Host?.FilesChanged();
    }

    void FillMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var items = Selected;
        void Add(string header, Action action, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        bool any = items.Count > 0;
        Add("열기", OpenSelected, any, "Enter");
        menu.Items.Add(new Separator());
        string? main = Host?.MainFolder;
        string mainName = main == null ? "" : $" ({FileSystem.DisplayName(main)})";
        Add("왼쪽 창으로 복사" + mainName, () => SendToMain(move: false), any && main != null);
        Add("왼쪽 창으로 이동" + mainName, () => SendToMain(move: true), any && main != null);
        menu.Items.Add(new Separator());
        Add("잘라내기", () => SetClipboard(cut: true), any, "Ctrl+X");
        Add("복사", () => SetClipboard(cut: false), any, "Ctrl+C");
        Add("붙여넣기", Paste, Clipboard.ContainsFileDropList(), "Ctrl+V");
        Add("삭제", () => Delete(permanently: false), any, "Del");
        Add("이름 바꾸기", Rename, items.Count == 1, "F2");
        menu.Items.Add(new Separator());
        Add("새 폴더", NewFolder, Folder.Length > 0 && !FileSystem.IsVirtual(Folder), "Ctrl+Shift+N");
        Add("새로 고침", () => Reload(), true, "F5");
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
        var paths = Selected.Select(i => i.FullPath).ToArray();
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
            : Folder.Length > 0 ? Folder : null;

    static DragDropEffects EffectFor(DragEventArgs e, string[] files, string folder)
    {
        DragDropEffects wanted = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy
            : e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) ? DragDropEffects.Move
            : files.All(f => string.Equals(Path.GetPathRoot(f), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Move
            : DragDropEffects.Copy;
        return e.AllowedEffects.HasFlag(wanted) ? wanted : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    static bool Invalid(string[] files, string folder) =>
        files.Any(f => string.Equals(f.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                       folder.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || DropFolder(e) is not string folder || Invalid(files, folder))
            return;
        var effect = EffectFor(e, files, folder);
        bool sameFolder = files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        e.Effects = sameFolder && effect == DragDropEffects.Move ? DragDropEffects.None : effect;
    }

    async void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Host == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files || DropFolder(e) is not string folder || Invalid(files, folder))
            return;
        var effect = EffectFor(e, files, folder);
        if (effect == DragDropEffects.None)
            return;
        if (await FileOps.TransferAsync(Host.WindowHandle, files, folder, move: effect == DragDropEffects.Move))
            AfterChange();
    }

    /// <summary>Copies or moves files from the main list into this pane's folder.</summary>
    public async Task ReceiveAsync(IReadOnlyList<string> paths, bool move)
    {
        if (Folder.Length == 0 || Host == null || paths.Count == 0)
            return;
        if (await FileOps.TransferAsync(Host.WindowHandle, paths, Folder, move))
            AfterChange();
    }
}
