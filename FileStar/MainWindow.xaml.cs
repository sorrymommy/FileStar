using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStar;

public partial class MainWindow : Window, IFolderPaneHost
{
    static readonly Dictionary<string, string> ColumnLabels = new()
    {
        ["Name"] = "이름",
        ["Note"] = "메모",
        ["Tags"] = "태그",
        ["Ext"] = "확장자",
        ["Size"] = "크기",
        ["Type"] = "유형",
        ["Folder"] = "위치",
        ["Compare"] = "비교",
        ["Modified"] = "수정한 날짜",
        ["Created"] = "만든 날짜",
        ["Accessed"] = "액세스한 날짜",
        ["Attributes"] = "특성",
    };

    readonly ObservableCollection<BrowserTab> _tabs = new();
    readonly ObservableCollection<FolderNode> _treeRoots = new();
    readonly DispatcherTimer _watchTimer;
    BrowserTab _active = null!;
    FolderNode? _favoritesNode, _pcNode, _selectedNode, _contextNode;
    List<FolderNode> _pathChain = new();

    /// <summary>The favorite folder the current location was reached through (tree stays in 즐겨찾기), or null for 내 PC.</summary>
    FolderNode? _favoriteAnchor;
    ListCollectionView? _view;
    CancellationTokenSource? _loadCts;
    FileSystemWatcher? _watcher;
    string _sortKey;

    // Memo pane state: which path the editor is showing, the memo as saved, whether 수정 is on, and unsaved tag changes.
    string? _notePath;
    FileItem? _noteItem;
    string _savedNoteText = "";
    bool _editingNote, _noteDirty, _loadingNote;
    double _notePaneWidth;
    readonly ObservableCollection<string> _noteTags = new();
    readonly DispatcherTimer _propsTimer;
    string? _propsPath;
    CancellationTokenSource? _folderSizeCts;
    bool _sortDescending;

    public MainWindow()
    {
        Perf.Log("--- start (window constructor)");
        ContentRendered += (_, _) => Perf.Log("first frame on screen");
        InitializeComponent();

        var settings = AppSettings.Load();
        Width = settings.Width;
        Height = settings.Height;
        // Back where it was, unless that spot is no longer on any screen (e.g. a monitor was unplugged).
        if (settings.Left is double left && settings.Top is double top &&
            new Rect(left, top, Width, Height).IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        _propLabelWidth = Math.Clamp(settings.PropLabelWidth, 50, 400);
        _hiddenProps = new HashSet<string>(settings.HiddenProps);
        if (settings.Maximized)
            WindowState = WindowState.Maximized;
        TreeColumn.Width = new GridLength(Math.Max(120, settings.TreeWidth));
        SetDarkMode(settings.DarkMode);
        FileSystem.ShowHidden = settings.ShowHidden;
        ShowHiddenMenu.IsChecked = settings.ShowHidden;
        UpdateHiddenButton(settings.ShowHidden);
        _sortKey = ColumnLabels.ContainsKey(settings.SortKey) ? settings.SortKey : "Name";
        _sortDescending = settings.SortDescending;
        UpdateSortHeaders();
        ApplyColumnSettings(settings.Columns);

        NotesStore.Load();
        RecentItems.Load(settings);
        NotesStore.Changed += OnNotesChanged;
        UndoStack.Changed += () => Dispatcher.BeginInvoke(UpdateUndoMenu);
        SetNoteMarks(settings.ShowNoteMarks);
        (_searchFields, _searchSubfolders) = SearchLocation.ParseOptions(settings.SearchOptions ?? "ntms");
        _windowsMenuHover.Tick += WindowsMenuHover_Tick;
        // Once the window is up and idle, load the Windows menu's shell extensions in the background.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var sampleFile = _view?.SourceCollection.Cast<FileItem>().FirstOrDefault(i => !i.IsDirectory)?.FullPath
                             ?? Environment.ProcessPath;
            ShellContextMenu.PrewarmInBackground(sampleFile,
                InVirtual ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _active.FullPath);
        });
        TagChips.ItemsSource = _noteTags;
        // A little after start-up, quietly look for a new version (the first window only).
        if (!Updater.CheckedAtStartup)
        {
            Updater.CheckedAtStartup = true;
            Loaded += (_, _) => _ = CheckForUpdatesAsync(quiet: true);
        }
        // Shell property lookups take a moment; wait until selection settles (e.g. while arrowing through files).
        _propsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _propsTimer.Tick += (_, _) =>
        {
            _propsTimer.Stop();
            UpdatePropertiesPane();
            UpdatePreviewPane();
        };
        _notePaneWidth = Math.Max(200, settings.NotePaneWidth);
        InfoTabs.SelectedIndex = Math.Clamp(settings.InfoTab, 0, InfoTabs.Items.Count - 1);
        SetNotePaneVisible(settings.NotePaneVisible);

        // Save the session as it changes, not only on a normal close: a Windows restart or sign-out skips
        // Closing, and so would a crash. Moves and tab changes save shortly after; everything else is
        // picked up by the periodic save.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };
        var periodicSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        periodicSave.Tick += (_, _) => SaveSettings();
        periodicSave.Start();
        Application.Current.SessionEnding += (_, _) => SaveSettings();
        // Which list has the keyboard decides the "active pane" line in 두 창 보기.
        FileList.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                SetActiveList(false);
        };
        DualPane.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                SetActiveList(true);
        };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        // Coming back to this window: another FileStar window may have changed memos, and files may have been
        // moved in another program while FileStar was closed or in the background.
        Activated += async (_, _) =>
        {
            if (NotesStore.ReloadIfChangedElsewhere())
                RefreshNoteColumns();
            await AutoRepairNotesAsync();
        };
        if (NotesStore.StorageError is string storageError)
            Loaded += (_, _) => MessageBox.Show(this, storageError + "\n\n이번 실행 동안 바꾼 메모는 저장되지 않습니다.",
                AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);

        // Debounce file system change notifications into a single refresh.
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watchTimer.Tick += (_, _) =>
        {
            _watchTimer.Stop();
            if (_renamingItem != null)
            {
                _watchTimer.Start();   // a reload would throw away the name being typed; try again shortly
                return;
            }
            Refresh(reloadTree: false);
        };

        TabStrip.ItemsSource = _tabs;
        FolderTree.ItemsSource = _treeRoots;
        FavoritesStore.Load();
        FavoritesStore.Changed += OnFavoritesChanged;
        BuildTree();
        RebuildFavoritesMenu();
        SetGoMenuIcons();

        static bool Openable(string path) => FileSystem.IsVirtual(path) || Directory.Exists(path);
        var startPaths = Environment.GetCommandLineArgs().Skip(1).Where(Openable).ToList();
        var pinned = new HashSet<int>(settings.PinnedTabs ?? new());
        var paths = startPaths.Count > 0
            ? startPaths.Select(p => (Path: p, Pinned: false)).ToList()
            : settings.Tabs.Select((p, i) => (Path: p, Pinned: pinned.Contains(i))).Where(t => Openable(t.Path)).ToList();
        if (paths.Count == 0)
            paths.Add((Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false));
        foreach (var (path, isPinned) in paths)
            _tabs.Add(new BrowserTab(FileSystem.IsThisPc(path) ? FileSystem.ThisPc : FileSystem.IsVirtual(path) ? path : FileSystem.Normalize(path)) { IsPinned = isPinned });
        int active = startPaths.Count > 0 ? 0 : Math.Clamp(settings.ActiveTab, 0, _tabs.Count - 1);
        ActivateTab(_tabs[active]);
        _dualRatio = Math.Clamp(settings.DualPaneRatio, 0.15, 0.85);
        if (settings.DualPaneVisible)
            SetDualPane(true, settings.DualPanePath);
    }

    // ───────────── Tree ─────────────

    void BuildTree()
    {
        _treeRoots.Clear();
        _selectedNode = null;
        _pathChain = new();
        _favoritesNode = FolderNode.Group("즐겨찾기", FavoritesStore.Items.Select(FolderNode.FromFavorite), ShellInfo.FavoritesIcon);
        _treeRoots.Add(_favoritesNode);

        _pcNode = FolderNode.Group("내 PC", DriveInfo.GetDrives().Select(FolderNode.Drive));
        _treeRoots.Add(_pcNode);
        // Tags start folded: there can be many.
        _tagsNode = FolderNode.Group("태그", NotesStore.TagCounts().Select(t => FolderNode.Tag(t.Tag, t.Count)), ShellInfo.TagIcon);
        _tagsNode.IsExpanded = false;
        _treeRoots.Add(_tagsNode);
        if (_favoriteAnchor != null)
            _favoriteAnchor = FindFavoriteFolderNode(_favoriteAnchor.FullPath!);
    }

    FolderNode? _tagsNode;

    /// <summary>Memos changed: refresh the tag list and counts, keeping the branch open/closed and the selection.</summary>
    void RebuildTagsBranch()
    {
        if (_tagsNode == null)
            return;
        var tags = NotesStore.TagCounts();
        var current = _tagsNode.Children.Select(n => n.Name).ToList();
        var updated = tags.Select(t => FolderNode.Tag(t.Tag, t.Count)).ToList();
        if (current.SequenceEqual(updated.Select(n => n.Name)))
            return;
        string? selectedTag = _selectedNode?.TagName;
        _quietTreeSelection = true;
        try
        {
            _tagsNode.Children.Clear();
            foreach (var node in updated)
                _tagsNode.Children.Add(node);
        }
        finally
        {
            _quietTreeSelection = false;
        }
        if (selectedTag != null)
            SyncTree(_active.FullPath);
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it.</summary>
    void SyncTree(string path)
    {
        if (_pcNode == null)
            return;
        if (FileSystem.IsThisPc(path))
        {
            _favoriteAnchor = null;
            SelectTreePath(_pcNode, new List<FolderNode> { _pcNode });
            return;
        }
        if (SearchLocation.TryParse(path) is { } search)
        {
            _favoriteAnchor = null;
            // A tag list selects its tag in the 태그 branch; other searches aren't in the tree.
            var tagNode = search.Kind == SearchKind.Tag
                ? _tagsNode?.Children.FirstOrDefault(n => string.Equals(n.TagName, search.Query, StringComparison.CurrentCultureIgnoreCase))
                : null;
            if (tagNode != null)
            {
                _tagsNode!.IsExpanded = true;
                SelectTreePath(tagNode, new List<FolderNode> { _tagsNode, tagNode });
            }
            else
            {
                if (_selectedNode != null)
                    _selectedNode.IsSelected = false;
                _selectedNode = null;
                MarkPath(new List<FolderNode>());
            }
            return;
        }

        // Browsing through a favorite: keep following the path inside that 즐겨찾기 branch.
        // Only once the location leaves the favorite folder does the tree switch to 내 PC.
        if (_favoriteAnchor != null && IsSameOrUnder(path, _favoriteAnchor.FullPath!))
        {
            var favoriteChain = _favoriteAnchor.SelfAndAncestors().Reverse().ToList();
            foreach (var group in favoriteChain.Take(favoriteChain.Count - 1))
                group.IsExpanded = true;
            SelectTreePath(Descend(_favoriteAnchor, path[_favoriteAnchor.FullPath!.TrimEnd('\\').Length..], favoriteChain), favoriteChain);
            return;
        }
        _favoriteAnchor = null;

        string root = Path.GetPathRoot(path) ?? "";
        var drive = _pcNode.Children.FirstOrDefault(n => string.Equals(n.FullPath, root, StringComparison.OrdinalIgnoreCase));
        if (drive == null)
        {
            MarkPath(new List<FolderNode>());
            return;
        }
        _pcNode.IsExpanded = true;
        var chain = new List<FolderNode> { _pcNode, drive };
        SelectTreePath(Descend(drive, path[root.Length..], chain), chain);
    }

    /// <summary>Expands from <paramref name="start"/> along the relative path, appending each node found to <paramref name="chain"/>.</summary>
    static FolderNode Descend(FolderNode start, string relativePath, List<FolderNode> chain)
    {
        var node = start;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                break;   // e.g. a hidden folder that the tree doesn't list
            node = next;
            chain.Add(node);
        }
        return node;
    }

    static bool IsSameOrUnder(string path, string folder)
    {
        string root = folder.TrimEnd('\\');
        return string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The favorite folder node (in the 즐겨찾기 branch) for a path, if there is one.</summary>
    FolderNode? FindFavoriteFolderNode(string path)
    {
        IEnumerable<FolderNode> Walk(FolderNode group) => group.Children
            .Where(c => c.Favorite != null)
            .SelectMany(c => c.Favorite!.IsGroup ? Walk(c) : new[] { c });
        return _favoritesNode == null ? null
            : Walk(_favoritesNode).FirstOrDefault(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Goes to a favorite and keeps the tree inside its 즐겨찾기 branch.</summary>
    void NavigateToFavorite(string path)
    {
        if (DualActive)
        {
            DualPane.Navigate(path);
            return;
        }
        _favoriteAnchor = FindFavoriteFolderNode(path);
        if (string.Equals(FileSystem.Normalize(path), _active.FullPath, StringComparison.OrdinalIgnoreCase))
            SyncTree(_active.FullPath);   // already there: just move the tree selection
        else
            Navigate(path);
    }

    void SelectTreePath(FolderNode node, List<FolderNode> chain)
    {
        if (_selectedNode != null && _selectedNode != node)
            _selectedNode.IsSelected = false;
        _selectedNode = node;
        node.IsSelected = true;
        MarkPath(chain);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => BringTreeChainIntoView(chain));
    }

    /// <summary>
    /// Colors the tree lines from the root down to the current folder: each node on <paramref name="chain"/>
    /// gets a red branch, and its parent's vertical line is red down to it.
    /// </summary>
    void MarkPath(List<FolderNode> chain)
    {
        foreach (var old in _pathChain)
        {
            old.IsOnPath = false;
            foreach (var child in old.Children)
                child.PathLineTop = child.PathLineBottom = false;
        }
        _pathChain = chain;
        for (int i = 0; i < chain.Count; i++)
        {
            chain[i].IsOnPath = true;
            if (i + 1 >= chain.Count)
                break;
            int target = chain[i].Children.IndexOf(chain[i + 1]);
            for (int j = 0; j <= target; j++)
            {
                chain[i].Children[j].PathLineTop = true;
                chain[i].Children[j].PathLineBottom = j < target;
            }
        }
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
            if (_pathChain.Count > 0 && container.DataContext == _pathChain[^1])
                container.BringIntoView();   // vertical only (TreeItem_RequestBringIntoView, TreeScroller_ScrollChanged)
        });
    }

    static TreeViewItem? GetTreeContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem existing)
            return existing;

        parent.ApplyTemplate();
        var presenter = FindVisualChild<ItemsPresenter>(parent);
        if (presenter == null)
        {
            parent.UpdateLayout();
            presenter = FindVisualChild<ItemsPresenter>(parent);
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

    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
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

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode node)
            return;
        _selectedNode = node;
        if (_quietTreeSelection)
            return;
        // Remember whether this node is inside a favorite, so later moves stay in that branch.
        _favoriteAnchor = node.SelfAndAncestors().FirstOrDefault(n => n.Favorite is { IsGroup: false });
        if (!node.IsVirtual)
            Go(node.FullPath!);
        else if (node == _pcNode)
            Navigate(FileSystem.ThisPc);
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

    bool _bringingTreeRow;

    /// <summary>
    /// The tree only scrolls sideways when the user does it (its scrollbar, the mouse wheel). Expanding a
    /// favorite or selecting a folder with a long name used to scroll it right and then back, so it shook.
    /// </summary>
    ScrollViewer? _treeScroller;
    double _treeHorizontalOffset;
    DateTime _treeWheelAt;

    void FolderTree_Loaded(object sender, RoutedEventArgs e)
    {
        if (_treeScroller != null)
            return;
        _treeScroller = FindVisualChild<ScrollViewer>(FolderTree);
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
        var node = e.CursorLeft < 0 ? _selectedNode : _contextNode;
        // Shift+right-click: straight to the Windows menu (with extended items), as in Explorer.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && node is { IsVirtual: false, IsTag: false })
        {
            e.Handled = true;
            ShowWindowsMenu(new[] { node.FullPath! }, ShellContextMenu.CursorPosition(), extended: true);
            return;
        }
        var menu = FolderTree.ContextMenu;
        menu.Items.Clear();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        string? path = node is { IsVirtual: false, IsTag: false } ? node.FullPath : null;
        if (path != null)
        {
            Add("새 탭에서 열기", () => NewTab(path));
            Add("Windows 탐색기에서 열기", () => OpenInWindowsExplorer(path, select: false));
            Add("경로 복사", () => Clipboard.SetText(path));
            Add("새 폴더", () => _ = CreateNewFolderAsync(path));
            menu.Items.Add(new Separator());
            var favoriteItem = new MenuItem();
            favoriteItem.Click += ActionMenuItem_Click;
            ConfigureFavoriteMenuItem(favoriteItem, path);
            menu.Items.Add(favoriteItem);
        }

        if (node?.Favorite is Favorite fav)
        {
            if (fav.IsGroup)
            {
                Add("이 그룹에 현재 폴더 추가", () => FavoritesStore.Add(_active.FullPath, fav), !FavoritesStore.Contains(_active.FullPath));
                Add("하위 그룹 만들기...", () => NewFavoriteGroup(fav));
                Add("그룹 이름 바꾸기...", () => RenameFavorite(fav));
                Add("그룹 삭제", () => DeleteFavorite(fav));
            }
            else
                Add("즐겨찾기 표시 이름 바꾸기...", () => RenameFavorite(fav));
        }
        else if (node == _favoritesNode)
            Add("새 그룹...", () => NewFavoriteGroup(null));
        Add("즐겨찾기 관리...", () => ManageFavorites(node?.Favorite));

        if (path != null)
        {
            bool movable = IsMovableFolder(node!) && node!.Favorite == null;
            menu.Items.Add(new Separator());
            Add("복사 (Ctrl+C)", () => TreeClipboard(cut: false, node));
            Add("잘라내기 (Ctrl+X)", () => TreeClipboard(cut: true, node), movable);
            Add("붙여넣기 (Ctrl+V)", () => TreePaste(node), Clipboard.ContainsFileDropList());
            Add("삭제 (Del)", () => TreeDelete(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), node), movable);
            Add("이름 바꾸기 (F2)", () => TreeRename(node), movable);
            menu.Items.Add(new Separator());
            Add("하위 폴더 새로 고침", () =>
            {
                node!.Reload();
                SyncTree(_active.FullPath);   // reloaded nodes need the path marking again
            });
            Add("속성", () => Native.ShowProperties(path));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateWindowsMenuItem(point => ShowWindowsMenu(new[] { path }, point, extended: false)));
        }
    }

    // ───────────── Windows (shell) context menu ─────────────

    readonly DispatcherTimer _windowsMenuHover = new() { Interval = TimeSpan.FromMilliseconds(200) };

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
            Icon = ThemedGlyph("\uE782", "Th.Accent", 13),
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
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
            ShowWindowsBackgroundMenu(screenPoint, extended);
        else
            ShowWindowsMenu(paths, screenPoint, extended);
    }

    // ── Building the Windows menu ahead of time ──
    // Building it is the slow part (~0.3 s: every installed extension adds items). While FileStar's own menu is
    // open, the user still has to move to "탐색기 메뉴", so it is built then and shown instantly on arrival.

    ShellContextMenu.PreparedMenu? _preparedShellMenu;

    void PrepareShellMenu(IReadOnlyList<string>? paths)
    {
        DiscardPreparedShellMenu();
        if (paths is not { Count: > 0 } && InVirtual)
            return;
        try
        {
            _preparedShellMenu = paths is { Count: > 0 }
                ? ShellContextMenu.PrepareForItems(this, paths, extended: false)
                : ShellContextMenu.PrepareForFolderBackground(this, _active.FullPath, extended: false);
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
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
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

    void FolderTreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var node = _contextNode ?? _selectedNode;
        if (node is not { IsVirtual: false, IsTag: false })
            return;
        var paths = new[] { node.FullPath! };
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (FolderTree.ContextMenu.IsOpen)
                PrepareShellMenu(paths);
        });
    }

    void ShowWindowsMenu(IReadOnlyList<string> paths, Point screenPoint, bool extended)
    {
        try
        {
            if (TakePreparedShellMenu(paths, background: false, extended) is { } prepared)
                ShellContextMenu.Show(this, prepared, screenPoint, HandleShellVerb);
            else
                ShellContextMenu.ShowForItems(this, paths, screenPoint, extended, HandleShellVerb);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "탐색기 메뉴를 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowWindowsBackgroundMenu(Point screenPoint, bool extended)
    {
        if (InVirtual)
            return;   // 내 PC and result lists are not folders; they have no background menu of their own
        try
        {
            if (TakePreparedShellMenu(new[] { _active.FullPath }, background: true, extended) is { } prepared)
                ShellContextMenu.Show(this, prepared, screenPoint, null);
            else
                ShellContextMenu.ShowForFolderBackground(this, _active.FullPath, screenPoint, extended);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "탐색기 메뉴를 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Verbs that only work inside Explorer's own window; everything else runs through the shell.</summary>
    bool HandleShellVerb(string verb, IReadOnlyList<string> paths)
    {
        if (verb.Equals("rename", StringComparison.OrdinalIgnoreCase) && paths.Count == 1)
        {
            // In the list: edit the name in place. Elsewhere (e.g. a tree folder): ask in a dialog.
            var row = _view?.SourceCollection.Cast<FileItem>()
                .FirstOrDefault(i => string.Equals(i.FullPath, paths[0], StringComparison.OrdinalIgnoreCase));
            if (row != null)
                BeginRename(row);
            else
                RenameItem(paths[0]);
            return true;
        }
        return false;
    }

    // ── In-place rename (F2) ──

    FileItem? _renamingItem;

    void BeginRename(FileItem item)
    {
        if (InThisPc)
            return;
        if (_renamingItem != null)
            _renamingItem.IsRenaming = false;
        _renamingItem = item;
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
        item.IsRenaming = true;   // the template swaps the name for an edit box; RenameBox_IsVisibleChanged focuses it
    }

    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || box.DataContext is not FileItem item)
            return;
        box.Text = item.Name;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            // Like Explorer: select the name without its extension.
            int length = item.IsDirectory || item.RawExtension.Length == 0 ? item.Name.Length : item.Name.Length - item.RawExtension.Length;
            box.Select(0, Math.Max(0, length));
        });
    }

    void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItem item } box)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(item, box.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndRename(item);
        }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.None ||
                 e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            // Rename file after file: ↑/Shift+Tab saves and edits the previous name, ↓/Tab the next one.
            e.Handled = true;
            bool previous = e.Key == Key.Up || e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift;
            if (NeighborOf(item, previous ? -1 : 1) is FileItem next)
                CommitRename(item, box.Text, thenRename: next.Name);
        }
    }

    /// <summary>The row <paramref name="offset"/> places away in the list as currently sorted and filtered.</summary>
    FileItem? NeighborOf(FileItem item, int offset)
    {
        if (_view == null)
            return null;
        int index = _view.IndexOf(item) + offset;
        return index >= 0 && index < _view.Count ? _view.GetItemAt(index) as FileItem : null;
    }

    void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere saves, as in Explorer.
        if (sender is TextBox { DataContext: FileItem item } box && item.IsRenaming)
            CommitRename(item, box.Text);
    }

    void EndRename(FileItem item)
    {
        item.IsRenaming = false;
        if (_renamingItem == item)
            _renamingItem = null;
        if (FileList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            container.Focus();
    }

    /// <param name="thenRename">Name of the row to edit next (↑/↓ while renaming), once this rename is done.</param>
    void CommitRename(FileItem item, string newName, string? thenRename = null)
    {
        if (!item.IsRenaming)
            return;   // already handled (Enter, then the focus loss that follows)
        newName = newName.Trim();
        EndRename(item);
        if (newName.Length == 0 || newName == item.Name)
        {
            if (thenRename != null)
                BeginRenameByName(thenRename);
            return;
        }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "이름에 사용할 수 없는 문자가 있습니다: \\ / : * ? \" < > |", AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            BeginRename(item);
            return;
        }
        MoveToNewName(item.FullPath, newName, thenRename);
    }

    void BeginRenameByName(string name)
    {
        if (_view?.Cast<FileItem>().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is FileItem row)
            BeginRename(row);
    }

    /// <summary>Renames on disk, keeps memo/tags attached, and reloads the list with the renamed item selected.</summary>
    async void MoveToNewName(string path, string newName, string? thenRename = null)
    {
        string newPath = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, newName);
        try
        {
            if (Directory.Exists(path))
                Directory.Move(path, newPath);
            else
                File.Move(path, newPath);
            NotesStore.Rename(path, newPath);
            UndoStack.Push(new RenameUndo(path, newPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "이름을 바꿀 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // The reload replaces every row, so the next row to edit is found again by name.
        await LoadFilesAsync(_active.FullPath, new[] { thenRename ?? newName });
        if (thenRename != null)
            BeginRenameByName(thenRename);
    }

    void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems is [var single])
            BeginRename(single);
    }

    // ── Drag-rectangle (rubber band) selection ──
    // Starts from empty space, or from a row anywhere except the file's icon/name (dragging there keeps the
    // list's normal behaviour). Every row the rectangle touches is selected; Ctrl adds to the existing selection.

    Point? _marqueeStart;
    bool _marqueeActive, _marqueeFromBlank;
    HashSet<FileItem> _marqueeBase = new();

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
            _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectedItems.ToHashSet() : new();
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
        if (_view == null)
            return;
        foreach (FileItem item in _view)
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
        UpdateWindowTitle();
        UpdateNotePane();
    }

    // ── Drag and drop ──
    // Out: the selected files go to any program that takes files (Explorer, the desktop, mail …); that program
    // decides between copy and move, as with Explorer. In: files dropped on the list, on a folder row or on a
    // tree folder are copied or moved there by FileOps, so memos and tags come along.

    Point? _dragStart;
    FileItem? _dragNarrowTo;

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
        var paths = SelectedItems.Select(i => i.FullPath).ToArray();
        if (paths.Length == 0)
            return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        // Drives and user folders listed in 내 PC can be copied or linked, never moved away.
        var allowed = InThisPc ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
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
        {
            for (var node = source; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is TreeViewItem { DataContext: FolderNode { IsVirtual: false, IsTag: false } folder })
                    return folder.FullPath;
            }
            return null;
        }
        if (source != null && ItemsControl.ContainerFromElement(FileList, source) is ListViewItem { DataContext: FileItem { IsDirectory: true } row })
            return row.FullPath;
        return InVirtual ? null : _active.FullPath;
    }

    /// <summary>Explorer's rule: Ctrl copies, Shift moves, otherwise move within a drive and copy across drives.</summary>
    static DragDropEffects DropEffectFor(DragEventArgs e, string[] files, string folder)
    {
        var allowed = e.AllowedEffects;
        DragDropEffects wanted;
        if (e.KeyStates.HasFlag(DragDropKeyStates.ControlKey))
            wanted = DragDropEffects.Copy;
        else if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
            wanted = DragDropEffects.Move;
        else
            wanted = files.All(f => string.Equals(Path.GetPathRoot(f), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Move : DragDropEffects.Copy;
        if (!allowed.HasFlag(wanted))
            wanted = allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        return wanted;
    }

    void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0 || DropFolderAt(e, sender) is not string folder)
            return;
        // Not into itself or one of its own subfolders, and not a move into the folder it's already in.
        if (files.Any(f => IsSameOrUnder(folder, f)))
            return;
        var effect = DropEffectFor(e, files, folder);
        bool sameFolder = files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (sameFolder && effect == DragDropEffects.Move)
            return;
        e.Effects = effect;
    }

    async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0 || DropFolderAt(e, sender) is not string folder)
            return;
        if (files.Any(f => IsSameOrUnder(folder, f)))
            return;
        var effect = DropEffectFor(e, files, folder);
        if (effect == DragDropEffects.None)
            return;
        e.Effects = effect;
        try
        {
            if (await FileOps.TransferAsync(WindowHandle, files, folder, move: effect == DragDropEffects.Move))
            {
                if (!InThisPc)
                    await LoadFilesAsync(_active.FullPath, files.Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList());
                _selectedNode?.Reload();
                SyncTree(_active.FullPath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, (effect == DragDropEffects.Move ? "옮길" : "복사할") + " 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void RenameItem(string path)
    {
        string oldName = Path.GetFileName(path.TrimEnd('\\'));
        if (InputDialog.Ask(this, "이름 바꾸기", "새 이름:", oldName) is not string newName || newName == oldName)
            return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "이름에 사용할 수 없는 문자가 있습니다: \\ / : * ? \" < > |", AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MoveToNewName(path, newName);
    }

    // ───────────── Favorites ─────────────

    void OnFavoritesChanged()
    {
        if (_favoritesNode != null)
        {
            if (_selectedNode?.Favorite != null)
                _selectedNode = null;
            _favoritesNode.Children.Clear();
            foreach (var node in FavoritesStore.Items.Select(FolderNode.FromFavorite))
                _favoritesNode.Children.Add(node);
            _favoritesNode.IsExpanded = true;
            // The branch was rebuilt: point the anchor at the new node for the same favorite (if it still exists).
            if (_favoriteAnchor != null)
            {
                _favoriteAnchor = FindFavoriteFolderNode(_favoriteAnchor.FullPath!);
                SyncTree(_active.FullPath);
            }
        }
        RebuildFavoritesMenu();
        UpdateFavoriteButton();
    }

    void RebuildFavoritesMenu()
    {
        FavoritesMenu.Items.Clear();
        void Add(string header, Action action, string gesture = "")
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            FavoritesMenu.Items.Add(item);
        }
        Add("현재 폴더를 즐겨찾기에 추가/제거", () => ToggleFavorite(_active.FullPath), "Ctrl+B");
        Add("즐겨찾기 관리...", () => ManageFavorites());
        if (FavoritesStore.Items.Count > 0)
            FavoritesMenu.Items.Add(new Separator());
        int shortcut = 0;
        AddFavoriteMenuItems(FavoritesMenu.Items, FavoritesStore.Items, ref shortcut);
    }

    void AddFavoriteMenuItems(ItemCollection target, IEnumerable<Favorite> favorites, ref int shortcut)
    {
        foreach (var fav in favorites)
        {
            var item = new MenuItem
            {
                Header = fav.Name.Replace("_", "__"),
                Icon = new Image { Source = fav.IsGroup ? ShellInfo.GetIcon("", true) : ShellInfo.GetIcon(fav.FullPath!, true, exact: true), Width = 16, Height = 16 },
            };
            if (fav.IsGroup)
            {
                AddFavoriteMenuItems(item.Items, fav.Children, ref shortcut);
                if (item.Items.Count == 0)
                    item.Items.Add(new MenuItem { Header = "(비어 있음)", IsEnabled = false });
            }
            else
            {
                shortcut++;
                string path = fav.FullPath!;
                item.ToolTip = path;
                item.InputGestureText = shortcut <= 9 ? $"Ctrl+{shortcut}" : "";
                item.Click += (_, _) => NavigateToFavorite(path);
            }
            target.Add(item);
        }
    }

    /// <summary>Menu entries for "add <paramref name="path"/> to which group?".</summary>
    List<object> AddToGroupChoices(string path)
    {
        var choices = new List<object>();
        var top = new MenuItem { Header = "맨 위 단계" };
        top.Click += (_, _) => FavoritesStore.Add(path);
        choices.Add(top);
        choices.Add(new Separator());
        foreach (var (group, label) in FavoritesStore.Groups())
        {
            var item = new MenuItem
            {
                Header = label.Replace("_", "__"),
                Icon = new Image { Source = ShellInfo.GetIcon("", true), Width = 16, Height = 16 },
            };
            item.Click += (_, _) => FavoritesStore.Add(path, group);
            choices.Add(item);
        }
        choices.Add(new Separator());
        var newGroup = new MenuItem { Header = "새 그룹을 만들어 추가..." };
        newGroup.Click += (_, _) =>
        {
            if (InputDialog.Ask(this, "새 그룹", "그룹 이름:", "새 그룹") is string name)
                FavoritesStore.Add(path, FavoritesStore.AddGroup(name));
        };
        choices.Add(newGroup);
        return choices;
    }

    /// <summary>
    /// Turns <paramref name="item"/> into "즐겨찾기에서 제거" or "즐겨찾기에 추가" (with a group submenu once groups exist).
    /// The item's Click must be wired to <see cref="ActionMenuItem_Click"/>.
    /// </summary>
    void ConfigureFavoriteMenuItem(MenuItem item, string path, string prefix = "")
    {
        item.Items.Clear();
        if (FavoritesStore.Contains(path))
        {
            item.Header = prefix + "즐겨찾기에서 제거";
            item.Tag = (Action)(() => FavoritesStore.Remove(path));
            return;
        }
        item.Header = prefix + "즐겨찾기에 추가";
        if (!FavoritesStore.Groups().Any())
        {
            item.Tag = (Action)(() => FavoritesStore.Add(path));
            return;
        }
        item.Tag = null;
        foreach (var choice in AddToGroupChoices(path))
            item.Items.Add(choice);
    }

    void ActionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Clicks on submenu entries bubble up here too; only act on the item itself.
        if (sender is MenuItem { Tag: Action action } item && e.OriginalSource == item)
            action();
    }

    void UpdateFavoriteButton()
    {
        FavoriteButton.IsEnabled = !FileSystem.IsVirtual(ActiveFolder);
        NewFolderButton.IsEnabled = !FileSystem.IsVirtual(ActiveFolder);
        bool isFavorite = FavoritesStore.Contains(ActiveFolder);
        FavoriteButton.Content = isFavorite ? "" : "";
        FavoriteButton.SetResourceReference(ForegroundProperty, isFavorite ? "Th.Favorite.On" : "Th.Accent");
        FavoriteButton.ToolTip = isFavorite ? "즐겨찾기에서 제거 (Ctrl+B)" : "즐겨찾기에 추가 (Ctrl+B)";
    }

    static void ToggleFavorite(string path)
    {
        if (FileSystem.IsVirtual(path))
            return;
        if (FavoritesStore.Contains(path))
            FavoritesStore.Remove(path);
        else
            FavoritesStore.Add(path);
    }

    void NewFavoriteGroup(Favorite? parent)
    {
        if (InputDialog.Ask(this, "새 그룹", "그룹 이름:", "새 그룹") is string name)
            FavoritesStore.AddGroup(name, parent);
    }

    void RenameFavorite(Favorite fav)
    {
        if (InputDialog.Ask(this, "이름 바꾸기", fav.IsGroup ? "그룹 이름:" : "트리에 표시할 이름:", fav.Name) is string name)
        {
            fav.Name = name;
            FavoritesStore.Save();
        }
    }

    void DeleteFavorite(Favorite fav)
    {
        int count = fav.SelfAndDescendants().Count() - 1;
        if (count > 0 && MessageBox.Show(this, $"'{fav.Name}' 그룹과 그 안의 항목 {count}개를 즐겨찾기에서 삭제할까요?\n(실제 폴더는 삭제되지 않습니다.)",
                "그룹 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        FavoritesStore.Remove(fav);
    }

    void ManageFavorites(Favorite? select = null)
    {
        new FavoritesWindow(_active.FullPath, select) { Owner = this }.ShowDialog();
    }

    void OpenFavorite(int index)
    {
        if (FavoritesStore.Folders.ElementAtOrDefault(index) is Favorite fav)
            NavigateToFavorite(fav.FullPath!);
    }

    void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        string path = ActiveFolder;
        if (FavoritesStore.Contains(path) || !FavoritesStore.Groups().Any())
        {
            ToggleFavorite(path);
            return;
        }
        // Groups exist: let the user pick where to put it.
        var menu = new ContextMenu { PlacementTarget = FavoriteButton, Placement = PlacementMode.Bottom, FontFamily = FontFamily, FontSize = FontSize };   // not the button's icon font
        foreach (var choice in AddToGroupChoices(path))
            menu.Items.Add(choice);
        menu.IsOpen = true;
    }

    /// <summary>The folder the file-list context menu acts on: the selected folder, or the current one.</summary>
    string FileContextFolder => SelectedItems is [{ IsDirectory: true } item] ? item.FullPath : _active.FullPath;

    // ───────────── Navigation ─────────────

    void Navigate(string path, string? selectName = null)
    {
        try
        {
            path = FileSystem.IsThisPc(path) ? FileSystem.ThisPc
                : FileSystem.IsVirtual(path) ? path
                : FileSystem.Normalize(path);
        }
        catch
        {
            return;
        }
        if (!FileSystem.IsVirtual(path) && !Directory.Exists(path))
        {
            MessageBox.Show(this, $"'{path}' 폴더를 찾을 수 없습니다.", AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(path, _active.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            if (selectName != null)
                SelectByNames(new[] { selectName });
            return;
        }

        _active.Back.Push(_active.FullPath);
        _active.Forward.Clear();
        _active.FullPath = path;
        ShowCurrent(selectName);
    }

    void GoBack()
    {
        if (DualActive)
        {
            DualPane.GoBack();
            return;
        }
        if (_active.Back.Count == 0)
            return;
        string previous = _active.FullPath;
        _active.Forward.Push(previous);
        _active.FullPath = _active.Back.Pop();
        ShowCurrent(ChildNameOf(previous, _active.FullPath));
    }

    void GoForward()
    {
        if (DualActive)
        {
            DualPane.GoForward();
            return;
        }
        if (_active.Forward.Count == 0)
            return;
        string previous = _active.FullPath;
        _active.Back.Push(previous);
        _active.FullPath = _active.Forward.Pop();
        ShowCurrent(ChildNameOf(previous, _active.FullPath));
    }

    void GoUp()
    {
        if (DualActive)
        {
            DualPane.GoUp();
            return;
        }
        if (CurrentSearch is { Kind: SearchKind.Here } search)
        {
            Navigate(search.Base);   // out of a search: back to the folder it searched
            return;
        }
        if (InVirtual)
            return;
        var parent = Directory.GetParent(_active.FullPath);
        if (parent != null)
            Navigate(parent.FullName, Path.GetFileName(_active.FullPath));
        else
            Navigate(FileSystem.ThisPc, FileSystem.DisplayName(_active.FullPath));   // above a drive is 내 PC, as in Explorer
    }

    /// <summary>If <paramref name="path"/> is directly inside <paramref name="folder"/>, its name (to reselect it).</summary>
    static string? ChildNameOf(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : null;

    /// <summary>Updates every view for the active tab's current folder.</summary>
    void ShowCurrent(string? selectName = null)
    {
        string path = _active.FullPath;
        if (_dualActive)
        {
            _dualActive = false;
            UpdateActivePane();
        }
        UpdateWindowTitle();
        AddressBox.Text = FileSystem.IsVirtual(path) ? FileSystem.DisplayName(path) : path;
        MainPaneHeaderText.Text = FileSystem.DisplayName(path);
        MainPaneHeader.ToolTip = AddressBox.Text;
        CrumbBar.ItemsSource = BuildCrumbs(path);
        BackButton.IsEnabled = _active.Back.Count > 0;
        ForwardButton.IsEnabled = _active.Forward.Count > 0;
        UpButton.IsEnabled = !InVirtual || CurrentSearch is { Kind: SearchKind.Here };
        ShowAutoColumn("Folder", CurrentSearch != null);
        UpdateSearchPanel();
        UpdateListNoteMarks();
        UpdateFavoriteButton();
        if (!InVirtual)
            RecentItems.AddFolder(path);
        ScheduleSave();

        long t0 = Perf.Now;
        SyncTree(path);
        long tTree = Perf.Now;
        WatchFolder(path);
        UpdateDriveText(path);
        long tMisc = Perf.Now;
        var load = LoadFilesAsync(path, selectName == null ? null : new[] { selectName });
        if (Perf.Enabled)
        {
            load.ContinueWith(_ =>
            {
                long tLoaded = Perf.Now;
                // ContextIdle: after layout and rendering of the new list.
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                    Perf.Log($"navigate {Path.GetFileName(path)}: tree {tTree - t0}, watch/drive {tMisc - tTree}, " +
                             $"list load {tLoaded - tMisc}, render {Perf.Now - tLoaded} → total {Perf.Now - t0}"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    static List<Crumb> BuildCrumbs(string path)
    {
        var crumbs = new List<Crumb>();
        if (SearchLocation.TryParse(path) is { } search)
        {
            // A search sits under the folder it searched: 내 PC › … › 폴더 › '보고서' 검색
            if (search.Kind == SearchKind.Here && Directory.Exists(search.Base))
                crumbs.AddRange(BuildCrumbs(search.Base));
            crumbs.Add(new Crumb(search.Title, path, crumbs.Count > 0));
            return crumbs;
        }
        if (!FileSystem.IsThisPc(path))
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                crumbs.Insert(0, new Crumb(FileSystem.DisplayName(dir.FullName), dir.FullName, true));
            if (path.StartsWith(@"\\"))
            {
                crumbs[0] = crumbs[0] with { ShowSeparator = false };   // network paths aren't under 내 PC
                return crumbs;
            }
        }
        // Local paths start from 내 PC, as in Explorer: 내 PC › Windows (C:) › Users › …
        crumbs.Insert(0, new Crumb(FileSystem.ThisPc, FileSystem.ThisPc, false));
        return crumbs;
    }

    async Task LoadFilesAsync(string path, ICollection<string>? selectNames = null)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        StatusText.Text = "불러오는 중...";
        EmptyText.Text = "이 폴더는 비어 있습니다.";
        if (SearchLocation.TryParse(path) is { } search)
        {
            await LoadSearchAsync(search, path, cts, selectNames);
            return;
        }

        List<FileItem> items;
        try
        {
            items = await Task.Run(() => FileSystem.IsThisPc(path)
                ? FileSystem.GetThisPcItems()
                : FileSystem.GetEntries(path).Select(FileItem.From).OfType<FileItem>().ToList(), cts.Token);
            // Icons and type names off the UI thread, so drawing the list doesn't stall on the shell.
            await ShellInfo.PrefetchAsync(items);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
                return;
            _view = null;
            FileList.ItemsSource = null;
            EmptyText.Visibility = Visibility.Collapsed;
            StatusText.Text = "폴더를 열 수 없습니다: " + ex.Message;
            return;
        }
        if (cts.IsCancellationRequested || !string.Equals(_active.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var item in items)
        {
            item.Note = NotesStore.Get(item.FullPath);
            if (item.IsDirectory)
                item.ContainsNotes = NotesStore.FolderHasNotes(item.FullPath);
        }
        _view = new ListCollectionView(items)
        {
            CustomSort = new FileItemComparer(_sortKey, _sortDescending),
            Filter = FilterItem,
        };
        FileList.ItemsSource = _view;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selectNames is { Count: > 0 })
            SelectByNames(selectNames);
        else if (_view.Count > 0)
            FileList.ScrollIntoView(_view.GetItemAt(0));
        UpdateStatus();
        UpdateNotePane();
        RecomputeCompare();
    }

    /// <summary>
    /// Fills the list with a search's results as they are found: memos and tags first (instant), then names
    /// from walking the folder tree. Leaving the location cancels the walk.
    /// </summary>
    async Task LoadSearchAsync(SearchLocation search, string path, CancellationTokenSource cts, ICollection<string>? selectNames)
    {
        var items = new ObservableCollection<FileItem>();
        _view = new ListCollectionView(items)
        {
            CustomSort = new FileItemComparer(_sortKey, _sortDescending),
            Filter = FilterItem,
        };
        FileList.ItemsSource = _view;
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = "검색 중...";
        var notes = NotesStore.Snapshot();
        var skip = FileSystem.Options.AttributesToSkip;
        bool full;
        try
        {
            full = await Task.Run(() => SearchRunner.Run(search, notes, skip, batch =>
            {
                var made = batch
                    .Select(p => FileItem.From(Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p)))
                    .OfType<FileItem>()
                    .ToList();
                Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    foreach (var item in made)
                    {
                        item.Note = NotesStore.Get(item.FullPath);
                        if (item.IsDirectory)
                            item.ContainsNotes = NotesStore.FolderHasNotes(item.FullPath);
                        items.Add(item);
                    }
                    StatusText.Text = $"검색 중... {items.Count:N0}개 찾음";
                });
            }, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                StatusText.Text = "검색할 수 없습니다: " + ex.Message;
            return;
        }
        if (cts.IsCancellationRequested || !string.Equals(_active.FullPath, path, StringComparison.Ordinal))
            return;
        EmptyText.Text = search.Query.Length == 0 && search.Kind != SearchKind.Tag ? "검색어를 입력하고 Enter를 누르세요." : "찾은 항목이 없습니다.";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectNames is { Count: > 0 })
            SelectByNames(selectNames);
        UpdateStatus();
        StatusText.Text += full ? $" · 결과가 많아 {SearchRunner.MaxResults:N0}개까지만 표시 (검색어를 좁혀 보세요)" : $" · {search.Description}";
        UpdateNotePane();
    }

    /// <summary>The search or tag list the active tab shows, if any.</summary>
    SearchLocation? CurrentSearch => SearchLocation.TryParse(_active?.FullPath);

    /// <summary>The active tab shows a list rather than a real folder (내 PC, search or tag results).</summary>
    bool InVirtual => FileSystem.IsVirtual(_active?.FullPath);

    void OpenItemLocation_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems.FirstOrDefault() is FileItem item)
            Navigate(item.FolderPath, item.Name);
    }

    void SelectByNames(ICollection<string> names)
    {
        if (_view == null)
            return;
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        FileList.SelectedItems.Clear();
        FileItem? first = null;
        foreach (FileItem item in _view)
        {
            if (!set.Contains(item.Name))
                continue;
            FileList.SelectedItems.Add(item);
            first ??= item;
        }
        if (first != null)
        {
            FileList.ScrollIntoView(first);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (FileList.ItemContainerGenerator.ContainerFromItem(first) is ListViewItem container && FileList.IsKeyboardFocusWithin)
                    container.Focus();
            });
        }
    }

    void Refresh(bool reloadTree = true)
    {
        if (reloadTree)
        {
            _selectedNode?.Reload();
            SyncTree(_active.FullPath);
        }
        var selected = FileList.SelectedItems.Cast<FileItem>().Select(f => f.Name).ToList();
        UpdateDriveText(_active.FullPath);
        _ = LoadFilesAsync(_active.FullPath, selected);
    }

    void WatchFolder(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes,
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
            // Keep memos attached to files renamed here (by this app or any other program).
            watcher.Renamed += (_, e) => Dispatcher.BeginInvoke(() => NotesStore.Rename(e.OldFullPath, e.FullPath));
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // Some locations (e.g. certain network shares) cannot be watched; F5 still works.
        }
    }

    void SetShowHidden(bool show)
    {
        FileSystem.ShowHidden = show;
        ShowHiddenMenu.IsChecked = show;
        UpdateHiddenButton(show);
        ScheduleSave();
        BuildTree();
        SyncTree(_active.FullPath);
        Refresh(reloadTree: false);
    }

    void UpdateHiddenButton(bool show)
    {
        HiddenGlyph.Text = show ? "" : "";   // slashed eye = hide them again, eye = show them
        HiddenToggle.ToolTip = show ? "숨김 파일 숨기기 (Ctrl+H)" : "숨김 파일 표시 (Ctrl+H)";
    }

    // ───────────── Tabs ─────────────

    void ActivateTab(BrowserTab tab)
    {
        if (_active != null)
            _active.IsActive = false;
        _active = tab;
        tab.IsActive = true;
        ShowCurrent();
    }

    void NewTab(string path)
    {
        var tab = new BrowserTab(FileSystem.IsThisPc(path) ? FileSystem.ThisPc : FileSystem.IsVirtual(path) ? path : FileSystem.Normalize(path));
        _tabs.Insert(_tabs.IndexOf(_active) + 1, tab);
        ActivateTab(tab);
    }

    void CloseTab(BrowserTab tab)
    {
        if (tab.IsPinned)
        {
            ShowStatusNote("고정된 탭은 닫을 수 없습니다. 탭의 핀을 눌러 고정을 해제하세요.");
            return;
        }
        if (_tabs.Count <= 1)
            return;
        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (tab == _active)
            ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        ScheduleSave();
    }

    void CycleTab(int step)
    {
        int index = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
    }

    void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab } && tab != _active)
            ActivateTab(tab);
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BrowserTab tab })
            CloseTab(tab);
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
            CloseTab(tab);
    }

    void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            tab.IsPinned = !tab.IsPinned;
            ScheduleSave();
        }
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => NewTab(_active.FullPath);
    void CloseActiveTab_Click(object sender, RoutedEventArgs e) => CloseTab(_active);

    // ───────────── File list ─────────────

    List<FileItem> SelectedItems => FileList.SelectedItems.Cast<FileItem>().ToList();

    void OpenSelected()
    {
        var items = SelectedItems;
        if (items.Count == 1 && items[0].IsDirectory)
        {
            Navigate(items[0].FullPath);
            return;
        }
        foreach (var item in items)
        {
            if (item.IsDirectory)
                NewTab(item.FullPath);
            else
                ShellOpen(item.FullPath);
        }
    }

    public void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            RecentItems.AddFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "열 수 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void OpenInWindowsExplorer(string path, bool select)
    {
        if (FileSystem.IsThisPc(path))
            Process.Start("explorer.exe", "shell:MyComputerFolder");
        else if (SearchLocation.TryParse(path) is { } search)
        {
            if (search.Kind == SearchKind.Here)
                Process.Start("explorer.exe", $"\"{search.Base}\"");
        }
        else
            Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
    }

    /// <summary>The active tab shows 내 PC (drives and user folders) rather than a real folder.</summary>
    bool InThisPc => _active?.IsThisPc == true;

    // ── Copy / cut / paste / delete ──
    // Windows' own file-operation engine (FileOps): same clipboard format, Recycle Bin, progress and
    // "file already exists" dialogs as Explorer — without the delay of building a context menu first.

    public IntPtr WindowHandle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <param name="verb">"copy" or "cut".</param>
    void ClipboardSelected(string verb)
    {
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0 || InThisPc)
            return;
        try
        {
            FileOps.SetClipboard(paths, cut: verb == "cut");
            StatusText.Text = $"{paths.Count:N0}개 항목을 " + (verb == "cut" ? "잘라냈습니다. 붙여넣으면 이동됩니다." : "복사했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, (verb == "cut" ? "잘라내기" : "복사") + "할 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void PasteIntoCurrentFolder()
    {
        if (InVirtual)
            return;
        string folder = _active.FullPath;
        try
        {
            if (await FileOps.PasteAsync(WindowHandle, folder) && string.Equals(folder, _active.FullPath, StringComparison.OrdinalIgnoreCase))
                await LoadFilesAsync(folder, SelectedItems.Select(i => i.Name).ToList());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "붙여넣을 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Delete → Recycle Bin, Shift+Delete → permanent; confirmations follow the Windows settings.</summary>
    async void DeleteSelected(bool permanently)
    {
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count == 0 || InThisPc)   // never offer to delete a drive or a user folder from 내 PC
            return;
        // Keep the cursor near where it was: select the row now at the same position.
        int index = FileList.SelectedIndex;
        string folder = _active.FullPath;
        try
        {
            if (!await FileOps.DeleteAsync(WindowHandle, paths, permanently))
                return;   // cancelled or failed (Windows has already said why)
            if (!string.Equals(folder, _active.FullPath, StringComparison.OrdinalIgnoreCase))
                return;
            await LoadFilesAsync(folder);
            if (FileList.Items.Count > 0)
            {
                FileList.SelectedIndex = Math.Min(index, FileList.Items.Count - 1);
                if (FileList.ItemContainerGenerator.ContainerFromIndex(FileList.SelectedIndex) is ListViewItem row)
                    row.Focus();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "삭제할 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void FileDelete_Click(object sender, RoutedEventArgs e) =>
        DeleteSelected(permanently: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    static bool IsTypingInTextBox =>
        Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox || Keyboard.FocusedElement is FrameworkElement { TemplatedParent: ComboBox };

    /// <summary>Ctrl+C/X/V act on files unless the user is typing in a text box (there they copy text).</summary>
    bool HandleFileClipboardKey(Key key)
    {
        if (IsTypingInTextBox)
            return false;
        switch (key)
        {
            case Key.C when FileList.IsKeyboardFocusWithin:
                ClipboardSelected("copy");
                return true;
            case Key.X when FileList.IsKeyboardFocusWithin:
                ClipboardSelected("cut");
                return true;
            case Key.V when FileList.IsKeyboardFocusWithin:
                PasteIntoCurrentFolder();
                return true;
            default:
                return false;
        }
    }

    // ── The same commands on the folder tree (Ctrl+C/X/V, Del, F2 while the tree has focus, or its menu) ──

    /// <summary>While set, tree selection changes (e.g. a reload dropping the selected node) don't navigate.</summary>
    bool _quietTreeSelection;

    /// <summary>The tree folder that keyboard commands act on: the selected node, if it is a real folder.</summary>
    FolderNode? TreeTarget => _selectedNode is { IsVirtual: false, IsTag: false } node ? node : null;

    /// <summary>Drives can be opened and pasted into, but not cut, deleted or renamed.</summary>
    static bool IsMovableFolder(FolderNode node) => !node.IsVirtual && !node.IsTag && !FileSystem.IsRoot(node.FullPath!);

    bool HandleTreeKey(Key key, ModifierKeys mods)
    {
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control, shift = mods == ModifierKeys.Shift;
        if (ctrl && key == Key.C) TreeClipboard(cut: false);
        else if (ctrl && key == Key.X) TreeClipboard(cut: true);
        else if (ctrl && key == Key.V) TreePaste();
        else if ((key == Key.Delete && (none || shift)) || (ctrl && key == Key.D)) TreeDelete(permanently: shift);
        else if (none && key == Key.F2) TreeRename();
        else
            return false;
        return true;
    }

    /// <param name="target">The right-clicked node; keyboard commands use the selected one.</param>
    void TreeClipboard(bool cut, FolderNode? target = null)
    {
        if ((target ?? TreeTarget) is not { IsVirtual: false, IsTag: false } node || cut && !IsMovableFolder(node))
            return;
        try
        {
            FileOps.SetClipboard(new[] { node.FullPath! }, cut);
            StatusText.Text = $"'{node.Name}' 폴더를 " + (cut ? "잘라냈습니다. 붙여넣으면 이동됩니다." : "복사했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, (cut ? "잘라내기" : "복사") + "할 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void TreePaste(FolderNode? target = null)
    {
        if ((target ?? TreeTarget) is not { IsVirtual: false, IsTag: false } node)
            return;
        string folder = node.FullPath!;
        try
        {
            if (!await FileOps.PasteAsync(WindowHandle, folder))
                return;
            node.Reload();
            node.IsExpanded = true;
            if (string.Equals(folder, _active.FullPath, StringComparison.OrdinalIgnoreCase))
                await LoadFilesAsync(folder);
            SyncTree(_active.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "붙여넣을 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void TreeDelete(bool permanently, FolderNode? target = null)
    {
        target ??= _selectedNode;
        if (target?.Favorite is Favorite fav)
        {
            // Favorites and their groups are FileStar's own list: Delete takes them off it, the real folder stays.
            if (fav.IsGroup)
                DeleteFavorite(fav);   // asks first when the group isn't empty
            else if (MessageBox.Show(this, $"'{fav.Name}'을(를) 즐겨찾기에서 뺄까요?\n\n실제 폴더는 지워지지 않습니다.", "즐겨찾기",
                         MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                DeleteFavorite(fav);
            return;
        }
        if (target is not { IsVirtual: false } node || !IsMovableFolder(node))
            return;
        string path = node.FullPath!;
        string parent = Path.GetDirectoryName(path)!;
        try
        {
            if (!await FileOps.DeleteAsync(WindowHandle, new[] { path }, permanently))
                return;   // cancelled or failed (Windows has already said why)
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "삭제할 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        node.Parent?.Reload();
        // The folder being shown (or one inside it) is gone: step out to the parent.
        if (IsSameOrUnder(_active.FullPath, path))
            Navigate(parent, Path.GetFileName(path));
        else
        {
            SyncTree(_active.FullPath);
            if (string.Equals(parent, _active.FullPath, StringComparison.OrdinalIgnoreCase))
                await LoadFilesAsync(parent);
        }
        FolderTree.Focus();
    }

    void TreeRename(FolderNode? target = null)
    {
        target ??= _selectedNode;
        if (target?.Favorite is Favorite fav)
        {
            RenameFavorite(fav);   // the display name in 즐겨찾기, not the folder on disk
            return;
        }
        if (target is not { IsVirtual: false } node || !IsMovableFolder(node))
            return;
        if (_renamingNode != null && _renamingNode != node)
            _renamingNode.IsRenaming = false;
        _renamingNode = node;
        node.IsRenaming = true;   // the template swaps the name for an edit box; TreeRenameBox_IsVisibleChanged focuses it
    }

    FolderNode? _renamingNode;

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
            CommitTreeRename(node, box.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndTreeRename(node);
        }
    }

    void TreeRenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere saves, as in Explorer.
        if (sender is TextBox { DataContext: FolderNode node } box && node.IsRenaming)
            CommitTreeRename(node, box.Text);
    }

    void EndTreeRename(FolderNode node)
    {
        node.IsRenaming = false;
        if (_renamingNode == node)
            _renamingNode = null;
        if (FolderTree.IsKeyboardFocusWithin || Keyboard.FocusedElement == null)
            FolderTree.Focus();
    }

    void CommitTreeRename(FolderNode node, string newName)
    {
        if (!node.IsRenaming)
            return;   // already handled (Enter, then the focus loss that follows)
        newName = newName.Trim();
        string path = node.FullPath!;
        EndTreeRename(node);
        if (newName.Length == 0 || newName == Path.GetFileName(path))
            return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "이름에 사용할 수 없는 문자가 있습니다: \\ / : * ? \" < > |", AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            TreeRename(node);
            return;
        }
        string newPath = Path.Combine(Path.GetDirectoryName(path)!, newName);
        try
        {
            Directory.Move(path, newPath);
            NotesStore.Rename(path, newPath);
            UndoStack.Push(new RenameUndo(path, newPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "이름을 바꿀 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool showing = IsSameOrUnder(_active.FullPath, path);
        _quietTreeSelection = true;
        try
        {
            node.Parent?.Reload();
        }
        finally
        {
            _quietTreeSelection = false;
        }
        if (showing)
        {
            // Keep showing the same folder under its new name, without adding a history step.
            _active.FullPath = newPath + _active.FullPath[path.Length..];
            ShowCurrent();
        }
        else
        {
            SyncTree(_active.FullPath);
            Refresh(reloadTree: false);
        }
        FolderTree.Focus();
    }

    void CopyPathsToClipboard()
    {
        var paths = SelectedItems.Select(i => i.FullPath).ToList();
        Clipboard.SetText(paths.Count > 0 ? string.Join(Environment.NewLine, paths) : _active.FullPath);
    }

    void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on column headers or empty space.
        if (e.ChangedButton == MouseButton.Left &&
            ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem)
            OpenSelected();
    }

    void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;   // typing in the rename box, not commands for the list
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.F2 && mods == ModifierKeys.None)
        {
            if (SelectedItems is [var single])
                BeginRename(single);
        }
        else if (key == Key.Enter && mods == ModifierKeys.Alt)
        {
            if (SelectedItems.FirstOrDefault() is FileItem item)
                Native.ShowProperties(item.FullPath);
        }
        else if (key == Key.Enter && mods == ModifierKeys.None)
            OpenSelected();
        else if (key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            CopyPathsToClipboard();
        else
            return;
        e.Handled = true;
    }

    /// <summary>Title bar shows the full path of the selected item, or of the current folder when nothing is selected.</summary>
    void UpdateWindowTitle()
    {
        var selected = InfoSelection;
        string path = selected.Count == 1 ? selected[0].FullPath : ActiveFolder;
        string extra = selected.Count > 1 ? $" ({selected.Count:N0}개 선택)" : "";
        Title = $"{(FileSystem.IsVirtual(path) ? FileSystem.DisplayName(path) : path)}{extra} - {AppPaths.AppName}";
    }

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        long t0 = Perf.Now;
        UpdateStatus();
        if (_marqueeActive)
            return;   // dragging a selection rectangle: the panes catch up when it ends
        long tStatus = Perf.Now;
        UpdateWindowTitle();
        UpdateNotePane();
        if (Perf.Enabled)
        {
            long tHandlers = Perf.Now;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                Perf.Log($"select {FileList.SelectedItems.Count} item(s): status {tStatus - t0}, panes {tHandlers - tStatus}, " +
                         $"render {Perf.Now - tHandlers} → total {Perf.Now - t0}"));
        }
    }

    // ───────────── Memo pane ─────────────

    void SetNotePaneVisible(bool visible)
    {
        if (!visible && NotePane.Visibility == Visibility.Visible)
        {
            ResolvePendingNoteEdit();
            CommitNote();
            ClearPreview();
            _notePaneWidth = NoteColumn.ActualWidth;
        }
        NoteColumn.Width = visible ? new GridLength(_notePaneWidth) : new GridLength(0);
        NoteColumn.MinWidth = visible ? 200 : 0;
        NoteSplitterColumn.Width = new GridLength(visible ? 5 : 0);
        NotePane.Visibility = NoteSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NotePaneMenu.IsChecked = visible;
        InfoPaneToggle.IsChecked = visible;
        if (visible && _active != null)
            UpdateNotePane();
    }

    /// <summary>Points the memo/tag editor at the selected item, or at the current folder when nothing is selected.</summary>
    void UpdateNotePane()
    {
        if (NotePane.Visibility != Visibility.Visible || _active == null)
            return;
        _propsTimer.Stop();
        _propsTimer.Start();

        var selected = InfoSelection;
        string? path = selected.Count switch
        {
            0 => InfoFolder,   // 내 PC or a result list can't carry a memo
            1 => selected[0].FullPath,
            _ => null,
        };

        // Same target (e.g. the list refreshed because a file changed): keep the editor and caret as they are.
        if (path != null && string.Equals(path, _notePath, StringComparison.OrdinalIgnoreCase))
        {
            _noteItem = selected.FirstOrDefault();
            return;
        }

        ResolvePendingNoteEdit();
        CommitNote();
        _notePath = path;
        _noteItem = selected.Count == 1 ? selected[0] : null;

        if (path == null && selected.Count == 0)
            SetPaneHeader(InfoIcon, InfoTitle, (CurrentSearch?.Description ?? "드라이브와 주요 폴더") + " · 항목을 선택하면 메모·태그를 남길 수 있습니다.");
        else if (path == null)
        {
            long total = selected.Sum(i => i.Size ?? 0);
            SetPaneHeader(null, $"{selected.Count:N0}개 항목 선택됨",
                $"파일 크기 합계 {FileSystem.FormatBytes(total)} · 메모와 태그는 한 항목씩 작성할 수 있습니다.");
        }
        else if (_noteItem is FileItem item)
        {
            SetPaneHeader(item.Icon, item.Name, item.SortGroup == 1 ? item.TypeName   // a drive in 내 PC
                : item.IsDirectory ? $"파일 폴더 · 수정 {item.ModifiedText}"
                : $"{item.TypeName} · {FileSystem.FormatBytes(item.Size ?? 0)} · 수정 {item.ModifiedText}");
        }
        else
            SetPaneHeader(InfoIcon, InfoTitle, "현재 폴더 (항목을 선택하지 않으면 이 폴더의 정보를 보여 줍니다)");

        var note = path == null ? null : NotesStore.Get(path);
        _loadingNote = true;
        _savedNoteText = NoteBox.Text = note?.Text ?? "";
        _loadingNote = false;
        _noteTags.Clear();
        foreach (var tag in note?.TagList ?? Array.Empty<string>())
            _noteTags.Add(tag);
        NoteBox.IsEnabled = TagInput.IsEnabled = path != null;
        ShowNoteStatus(note);
        UpdateNoteEditUi();
    }

    /// <summary>Every info-pane tab starts with the same item header.</summary>
    void SetPaneHeader(ImageSource? icon, string title, string subtitle)
    {
        NoteIcon.Source = PropsIcon.Source = PreviewIcon.Source = icon;
        NoteTitle.Text = PropsTitle.Text = PreviewTitle.Text = title;
        NoteSubtitle.Text = PropsSubtitle.Text = PreviewSubtitle.Text = subtitle;
    }

    void ShowNoteStatus(FileNote? note)
    {
        NoteStatus.Text = note == null ? "" : $"저장됨 · {note.Updated:yyyy-MM-dd HH:mm}";
        NoteDeleteButton.IsEnabled = note != null;
        UpdateStaleBanner(note);
    }

    /// <summary>"메모 갱신 필요": the file was saved after this memo was written or last confirmed.</summary>
    void UpdateStaleBanner(FileNote? note)
    {
        DateTime written = default;
        bool stale = _notePath != null && File.Exists(_notePath) &&
                     NotesStore.IsStale(note, written = File.GetLastWriteTime(_notePath), isDirectory: false);
        NoteStaleBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        if (stale)
            NoteStaleText.Text = $"메모를 쓴 뒤({note!.Updated:yyyy-MM-dd HH:mm})에 파일이 수정되었습니다({written:yyyy-MM-dd HH:mm}). " +
                                 "메모가 지금 내용과 다를 수 있습니다 — '수정'이나 'AI 요약'으로 고치거나, 맞으면 '확인함'을 누르세요.";
    }

    void NoteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_notePath == null || NotesStore.Confirm(_notePath) is not FileNote note)
            return;
        foreach (var row in _view?.SourceCollection.Cast<FileItem>() ?? [])
        {
            if (string.Equals(row.FullPath, _notePath, StringComparison.OrdinalIgnoreCase))
                row.Note = note;
        }
        ShowNoteStatus(note);
    }

    /// <summary>Saves a tag change right away. The memo text kept on disk is reused, so an unsaved memo edit is not saved with it.</summary>
    void CommitNote()
    {
        if (!_noteDirty || _notePath == null)
            return;
        _noteDirty = false;
        StoreNote(_savedNoteText);
    }

    void StoreNote(string text)
    {
        var note = NotesStore.Set(_notePath!, text, _noteTags);
        _savedNoteText = text;
        // The list may have been reloaded since the item was picked; update whichever row shows this path.
        foreach (var row in _view?.SourceCollection.Cast<FileItem>() ?? [])
        {
            if (string.Equals(row.FullPath, _notePath, StringComparison.OrdinalIgnoreCase))
                row.Note = note;
        }
        ShowNoteStatus(note);
    }

    // ── Memo editing: read-only until 수정, written only on 저장 ──

    bool NoteEditDirty => _editingNote && NoteBox.Text != _savedNoteText;

    void BeginNoteEdit()
    {
        if (_notePath == null)
            return;
        _editingNote = true;
        UpdateNoteEditUi();
        NoteBox.Focus();
        NoteBox.CaretIndex = NoteBox.Text.Length;
    }

    void SaveNoteEdit()
    {
        if (!_editingNote || _notePath == null)
            return;
        _editingNote = false;
        StoreNote(NoteBox.Text);
        UpdateNoteEditUi();
    }

    void CancelNoteEdit()
    {
        if (!_editingNote)
            return;
        _editingNote = false;
        _loadingNote = true;
        NoteBox.Text = _savedNoteText;
        _loadingNote = false;
        UpdateNoteEditUi();
        ShowNoteStatus(_notePath == null ? null : NotesStore.Get(_notePath));
    }

    /// <summary>Before the memo editor moves on (other item, pane hidden, app closing): ask what to do with an unsaved edit.</summary>
    void ResolvePendingNoteEdit()
    {
        if (!_editingNote)
            return;
        if (NoteEditDirty &&
            MessageBox.Show(this, "수정한 메모를 저장할까요?\n\n'아니요'를 누르면 수정한 내용을 버립니다.", "메모 저장",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            SaveNoteEdit();
        else
            CancelNoteEdit();
    }

    void UpdateNoteEditUi()
    {
        NoteBox.IsReadOnly = !_editingNote;
        NoteEditButton.Visibility = _editingNote ? Visibility.Collapsed : Visibility.Visible;
        NoteEditButton.IsEnabled = _notePath != null;
        NoteSaveButton.Visibility = NoteCancelButton.Visibility = _editingNote ? Visibility.Visible : Visibility.Collapsed;
        NoteBox.SetResourceReference(BorderBrushProperty, _editingNote ? "Th.Accent" : "Th.Memo.Border");
        NoteBox.BorderThickness = new Thickness(_editingNote ? 2 : 1);
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 && _notePath != null ? Visibility.Visible : Visibility.Collapsed;
        if (_editingNote)
            NoteStatus.Text = "수정 중 · 저장(Ctrl+S) / 취소(Esc)";
    }

    void NoteEdit_Click(object sender, RoutedEventArgs e) => BeginNoteEdit();
    void NoteSave_Click(object sender, RoutedEventArgs e) => SaveNoteEdit();
    void NoteCancel_Click(object sender, RoutedEventArgs e) => CancelNoteEdit();

    void NoteBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_editingNote)
            BeginNoteEdit();
    }

    void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 && _notePath != null ? Visibility.Visible : Visibility.Collapsed;
        if (!_loadingNote && _editingNote)
            NoteStatus.Text = NoteEditDirty ? "수정 중 (저장 안 됨) · 저장(Ctrl+S) / 취소(Esc)" : "수정 중 · 저장(Ctrl+S) / 취소(Esc)";
    }

    void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_notePath == null)
            return;
        if (MessageBox.Show(this, "이 항목의 메모와 태그를 모두 지울까요? (파일은 그대로 남습니다)", "메모·태그 삭제",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _editingNote = false;
        _loadingNote = true;
        NoteBox.Text = "";
        _loadingNote = false;
        _noteTags.Clear();
        _noteDirty = false;
        StoreNote("");
        UpdateNoteEditUi();
    }

    // ── Tags ──

    void AddTags(string input)
    {
        if (_notePath == null)
            return;
        bool added = false;
        foreach (var tag in NotesStore.ParseTags(input))
        {
            if (_noteTags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
                continue;
            _noteTags.Add(tag);
            added = true;
        }
        if (!added)
            return;
        _noteDirty = true;
        CommitNote();
    }

    void TagInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddTags(TagInput.Text);
        TagInput.Text = "";
        e.Handled = true;
    }

    void TagInput_DropDownOpened(object sender, EventArgs e)
    {
        // Suggest tags used elsewhere that this item doesn't have yet.
        TagInput.ItemsSource = NotesStore.AllTags()
            .Where(t => !_noteTags.Contains(t, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    void TagInput_DropDownClosed(object sender, EventArgs e)
    {
        if (TagInput.SelectedItem is string tag)
            AddTags(tag);
        TagInput.SelectedItem = null;
        TagInput.Text = "";
    }

    void TagRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        _noteTags.Remove(tag);
        _noteDirty = true;
        CommitNote();
    }

    void TagChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string tag })
            Navigate(SearchLocation.Tag(tag).Path);
    }

    // ── Properties tab ──

    void InfoTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != InfoTabs)
            return;
        if (_saveTimer != null)
            ScheduleSave();
        UpdatePropertiesPane();
        // Release the previewed file (and any preview handler process) as soon as the tab is left.
        if (PreviewTabVisible)
            UpdatePreviewPane(force: true);
        else
            ClearPreview();
    }

    bool PropertiesTabVisible => NotePane.Visibility == Visibility.Visible && InfoTabs.SelectedIndex == 1;

    /// <summary>Fills the 속성 tab for the selected item (or current folder); skipped while the tab is hidden.</summary>
    void UpdatePropertiesPane()
    {
        if (!PropertiesTabVisible || _active == null)
            return;
        var selected = InfoSelection;
        string? path = selected.Count switch
        {
            0 => InfoFolder,
            1 => selected[0].FullPath,
            _ => null,
        };
        _folderSizeCts?.Cancel();
        _propsPath = path;
        PropsPanel.Children.Clear();
        _propLabelColumns.Clear();
        _propLabelsSeen.Clear();
        _pendingPropSection = null;
        AddPropColumnHeader();

        if (path == null && selected.Count == 0)
        {
            // 내 PC: a summary of every drive.
            AddPropSection("드라이브");
            foreach (var drive in _view?.Cast<FileItem>().Where(i => i.SortGroup == 1) ?? [])
                AddPropRow(drive.Name, drive.TypeName);
            return;
        }
        if (path == null)
        {
            AddPropSection("선택 항목");
            AddPropRow("개수", $"{selected.Count:N0}개 (폴더 {selected.Count(i => i.IsDirectory):N0}, 파일 {selected.Count(i => !i.IsDirectory):N0})");
            long total = selected.Sum(i => i.Size ?? 0);
            AddPropRow("크기", $"{FileSystem.FormatBytes(total)} ({total:N0} 바이트)");
            return;
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        try { info.Refresh(); }
        catch { }
        bool isDirectory = info is DirectoryInfo;

        AddPropSection("일반");
        AddPropRow("DOS 이름", FileProperties.ShortName(path));
        AddPropRow("유형", ShellInfo.GetTypeName(isDirectory ? "" : Path.GetExtension(path), isDirectory));
        AddPropRow("위치", Path.GetDirectoryName(path) ?? path);
        if (info is FileInfo file && file.Exists)
        {
            AddPropRow("크기", $"{FileSystem.FormatBytes(file.Length)} ({file.Length:N0} 바이트)");
            AddPropRow("디스크 할당 크기", FileProperties.SizeOnDisk(path) is var (bytes, clusters)
                ? $"{FileSystem.FormatBytes(bytes)} ({bytes:N0} 바이트), {clusters:N0} 클러스터"
                : "-");
        }
        else if (isDirectory)
            AddFolderSizeRow(path);

        AddPropSection("날짜");
        AddPropRow("만든 날짜", FormatPropDate(() => info.CreationTime));
        AddPropRow("수정한 날짜", FormatPropDate(() => info.LastWriteTime));
        AddPropRow("액세스한 날짜", FormatPropDate(() => info.LastAccessTime));

        AddPropSection("특성");
        _propLabelsSeen.Add(AttributesPropKey);
        if (!_hiddenProps.Contains(AttributesPropKey))
        {
            FlushPropSection();
            uint attributes = FileProperties.RawAttributes(path);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var (label, flag) in FileProperties.Attributes)
            {
                bool on = (attributes & flag) != 0;
                grid.Children.Add(new CheckBox
                {
                    Content = label,
                    IsChecked = on,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Margin = new Thickness(0, 2, 6, 2),
                }.WithResource(Control.ForegroundProperty, on ? "Th.Text" : "Th.Text.Faint"));
            }
            PropsPanel.Children.Add(grid);
        }

        var details = FileProperties.ShellDetails(path);
        if (details.Count > 0)
        {
            AddPropSection("상세 정보 (Windows 속성)");
            foreach (var (name, value) in details)
                AddPropRow(name, value);
        }
    }

    static string FormatPropDate(Func<DateTime> getter)
    {
        try { return getter().ToString("yyyy-MM-dd tt h:mm:ss"); }
        catch { return "-"; }
    }

    // ── Which properties show, and how wide their names are (both saved with the window) ──

    const string AttributesPropKey = "특성";
    double _propLabelWidth = 110;
    HashSet<string> _hiddenProps = new();
    readonly List<ColumnDefinition> _propLabelColumns = new();
    /// <summary>Every property the current item has, shown or hidden, for the chooser menu.</summary>
    readonly List<string> _propLabelsSeen = new();
    string? _pendingPropSection;

    /// <summary>Sections appear only once one of their rows does, so hiding every row hides the heading too.</summary>
    void AddPropSection(string title) => _pendingPropSection = title;

    void FlushPropSection()
    {
        if (_pendingPropSection == null)
            return;
        PropsPanel.Children.Add(new TextBlock
        {
            Text = _pendingPropSection,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, PropsPanel.Children.Count <= 1 ? 0 : 12, 0, 4),
        }.WithResource(TextBlock.ForegroundProperty, "Th.Note.Header"));
        _pendingPropSection = null;
    }

    /// <summary>"속성 | 값" with a splitter between them: dragging it sets the name column for every row.</summary>
    void AddPropColumnHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var labelColumn = new ColumnDefinition { Width = new GridLength(_propLabelWidth), MinWidth = 50, MaxWidth = 400 };
        header.ColumnDefinitions.Add(labelColumn);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = "속성", FontSize = 11 }.WithResource(TextBlock.ForegroundProperty, "Th.Text.Faint"));
        var valueTitle = new TextBlock { Text = "값", FontSize = 11, Margin = new Thickness(2, 0, 0, 0) }.WithResource(TextBlock.ForegroundProperty, "Th.Text.Faint");
        Grid.SetColumn(valueTitle, 2);
        header.Children.Add(valueTitle);
        var splitter = new GridSplitter
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent,
            ToolTip = "끌어서 속성 이름 칸의 폭을 바꿉니다",
        };
        // A visible grip line in the middle of the transparent drag area.
        var grip = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false }
            .WithResource(Border.BackgroundProperty, "Th.Border");
        Grid.SetColumn(grip, 1);
        Grid.SetColumn(splitter, 1);
        header.Children.Add(grip);
        header.Children.Add(splitter);
        splitter.DragDelta += (_, _) =>
        {
            _propLabelWidth = labelColumn.ActualWidth;
            foreach (var column in _propLabelColumns)
                column.Width = new GridLength(_propLabelWidth);
        };
        splitter.DragCompleted += (_, _) => ScheduleSave();
        PropsPanel.Children.Add(header);
    }

    /// <summary>
    /// A label/value row; the value is a borderless read-only TextBox so it can be selected and copied.
    /// A property the user hid gets a box that is never shown.
    /// </summary>
    TextBox AddPropRow(string label, string value)
    {
        var box = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0),
        };
        if (!_propLabelsSeen.Contains(label))
            _propLabelsSeen.Add(label);
        if (_hiddenProps.Contains(label))
            return box;
        FlushPropSection();
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        var labelColumn = new ColumnDefinition { Width = new GridLength(_propLabelWidth) };
        _propLabelColumns.Add(labelColumn);
        row.ColumnDefinitions.Add(labelColumn);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent }
            .WithResource(TextBlock.ForegroundProperty, "Th.Text.Muted");
        var hide = new MenuItem { Header = $"'{label}' 숨기기" };
        hide.Click += (_, _) => SetPropVisible(label, false);
        name.ContextMenu = new ContextMenu { Items = { hide } };
        row.Children.Add(name);
        Grid.SetColumn(box, 2);
        row.Children.Add(box);
        PropsPanel.Children.Add(row);
        return box;
    }

    void SetPropVisible(string label, bool visible)
    {
        if (visible ? !_hiddenProps.Remove(label) : !_hiddenProps.Add(label))
            return;
        UpdatePropertiesPane();
        ScheduleSave();
    }

    void PropsChooser_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PropsChooserButton, Placement = PlacementMode.Bottom, FontFamily = FontFamily, FontSize = FontSize };   // not the button's icon font
        menu.Items.Add(new MenuItem { Header = "표시할 속성", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        // This item's properties, plus any hidden ones it doesn't have (so they can always be shown again).
        foreach (string label in _propLabelsSeen.Concat(_hiddenProps.Where(h => !_propLabelsSeen.Contains(h)).OrderBy(h => h)))
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = !_hiddenProps.Contains(label), StaysOpenOnClick = true };
            item.Checked += (_, _) => SetPropVisible(label, true);
            item.Unchecked += (_, _) => SetPropVisible(label, false);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var showAll = new MenuItem { Header = "모두 표시", IsEnabled = _hiddenProps.Count > 0 };
        showAll.Click += (_, _) =>
        {
            _hiddenProps.Clear();
            UpdatePropertiesPane();
            ScheduleSave();
        };
        menu.Items.Add(showAll);
        var resetWidth = new MenuItem { Header = "이름 칸 폭 기본값 (110)" };
        resetWidth.Click += (_, _) =>
        {
            _propLabelWidth = 110;
            UpdatePropertiesPane();
            ScheduleSave();
        };
        menu.Items.Add(resetWidth);
        menu.IsOpen = true;
    }

    /// <summary>Folder sizes need a full scan, so they are computed on request in the background.</summary>
    void AddFolderSizeRow(string path)
    {
        var valueBox = AddPropRow("크기", "");
        var row = (Grid)valueBox.Parent;
        row.Children.Remove(valueBox);
        var button = new Button { Content = "폴더 크기 계산", Padding = new Thickness(8, 1, 8, 1), HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(button, 2);
        row.Children.Add(button);
        button.Click += async (_, _) =>
        {
            _folderSizeCts?.Cancel();
            var cts = _folderSizeCts = new CancellationTokenSource();
            button.IsEnabled = false;
            button.Content = "계산 중...";
            try
            {
                var (bytes, files, folders) = await Task.Run(() => FileProperties.FolderSize(path, cts.Token), cts.Token);
                if (_propsPath != path)
                    return;
                row.Children.Remove(button);
                valueBox.Text = $"{FileSystem.FormatBytes(bytes)} ({bytes:N0} 바이트)\n파일 {files:N0}개, 폴더 {folders:N0}개";
                row.Children.Add(valueBox);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                button.Content = "계산 실패: " + ex.Message;
            }
        };
    }

    void EditNote()
    {
        SetNotePaneVisible(true);
        UpdateNotePane();
        if (NoteBox.IsEnabled)
            BeginNoteEdit();
    }

    void EditNote_Click(object sender, RoutedEventArgs e) => EditNote();

    // ── AI memo/tags ──

    async void RunAiSummary()
    {
        var selected = InfoSelection;
        if (selected.Count == 0 || InThisPc && !DualActive)
        {
            MessageBox.Show(this, "AI로 요약할 파일이나 폴더를 목록에서 선택하세요. (여러 개를 선택하면 차례로 처리하고, 폴더를 고르면 그 안의 파일을 찾아 목록에 올립니다.)",
                "AI로 메모·태그 만들기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Folders turn into the readable files inside them (the dialog asks about subfolders and existing memos).
        var files = await AiFolderDialog.ExpandAsync(this, selected);
        if (files is not { Count: > 0 })
            return;
        ResolvePendingNoteEdit();
        CommitNote();
        // Modeless, so browsing can continue while a long batch runs.
        new AiSummaryWindow(files, ReloadNotes) { Owner = this }.Show();
    }

    /// <summary>Re-reads memos/tags after they were changed outside the memo pane (e.g. by the AI window).</summary>
    void ReloadNotes()
    {
        ResolvePendingNoteEdit();
        foreach (var row in _view?.SourceCollection.Cast<FileItem>() ?? [])
            row.Note = NotesStore.Get(row.FullPath);
        _notePath = null;   // force the pane to reload
        UpdateNotePane();
        _view?.Refresh();
    }

    void AiSummary_Click(object sender, RoutedEventArgs e) => RunAiSummary();

    // ── Memos that lost their file (moved by another program) ──

    bool _repairRunning;
    DateTime _lastRepair;

    /// <summary>
    /// Quietly reattaches memos to files that another program moved (same name, found by file ID). Runs at
    /// start-up and whenever FileStar is switched back to, at most once every 30 seconds.
    /// </summary>
    async Task AutoRepairNotesAsync()
    {
        if (_repairRunning || DateTime.Now - _lastRepair < TimeSpan.FromSeconds(30))
            return;
        _repairRunning = true;
        _lastRepair = DateTime.Now;
        try
        {
            var snapshot = NotesStore.Snapshot();
            var result = await Task.Run(() => NotesRepair.Scan(snapshot));
            int relinked = NotesRepair.AutoRelink(result);
            if (relinked > 0)
            {
                RefreshNoteColumns();
                StatusText.Text = $"다른 곳으로 옮겨진 파일 {relinked:N0}개를 찾아 메모를 다시 연결했습니다.";
            }
        }
        catch { }
        finally
        {
            _repairRunning = false;
        }
    }

    /// <summary>Re-reads memos for the rows on screen without disturbing the memo editor.</summary>
    void RefreshNoteColumns()
    {
        foreach (var row in _view?.SourceCollection.Cast<FileItem>() ?? [])
            row.Note = NotesStore.Get(row.FullPath);
        if (!_editingNote)
        {
            _notePath = null;   // reload the pane for the same item
            UpdateNotePane();
        }
    }

    void NotesRepair_Click(object sender, RoutedEventArgs e) =>
        new NotesRepairWindow(RefreshNoteColumns) { Owner = this }.ShowDialog();

    void AiSettings_Click(object sender, RoutedEventArgs e) =>
        new AiSettingsWindow(AiSettings.Load()) { Owner = this }.ShowDialog();
    void ToggleNotePane_Click(object sender, RoutedEventArgs e) => SetNotePaneVisible(NotePane.Visibility != Visibility.Visible);

    // ── Toolbar ──

    void HiddenToggle_Click(object sender, RoutedEventArgs e) => SetShowHidden(!FileSystem.ShowHidden);

    // ── Memo/tag markers (the list and tree templates bind to ShowNoteMarks) ──

    public static readonly DependencyProperty ShowNoteMarksProperty =
        DependencyProperty.Register(nameof(ShowNoteMarks), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    /// <summary>Memo/tag markers before icons, bold names, and dots on folders that hold noted files.</summary>
    public bool ShowNoteMarks
    {
        get => (bool)GetValue(ShowNoteMarksProperty);
        set => SetValue(ShowNoteMarksProperty, value);
    }

    void NoteMarksToggle_Click(object sender, RoutedEventArgs e) => SetNoteMarks(!ShowNoteMarks);
    void NoteMarksMenu_Click(object sender, RoutedEventArgs e) => SetNoteMarks(NoteMarksMenu.IsChecked);

    /// <summary>
    /// The file list's markers: the toolbar toggle, except that search and tag results always show them
    /// (those lists are looked up by memo and tag, so which rows have them is the point).
    /// </summary>
    public static readonly DependencyProperty ListNoteMarksProperty =
        DependencyProperty.Register(nameof(ListNoteMarks), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public bool ListNoteMarks
    {
        get => (bool)GetValue(ListNoteMarksProperty);
        set => SetValue(ListNoteMarksProperty, value);
    }

    void UpdateListNoteMarks() => ListNoteMarks = ShowNoteMarks || CurrentSearch != null;

    void SetNoteMarks(bool show)
    {
        ShowNoteMarks = show;
        UpdateListNoteMarks();
        NoteMarksMenu.IsChecked = show;
        // Lit note (with rays) while markers show; the light goes out when they're off.
        NoteMarksRays.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NoteMarksToggle.ToolTip = show ? "메모·태그 표시 끄기 (Ctrl+Shift+H)" : "메모·태그 있는 파일 표시하기 (Ctrl+Shift+H)";
        if (_saveTimer != null)
            ScheduleSave();
    }

    /// <summary>Memos changed somewhere: folders on screen may have gained or lost their "holds noted files" dot.</summary>
    void OnNotesChanged()
    {
        if (_notesChangedQueued)
            return;
        _notesChangedQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _notesChangedQueued = false;
            foreach (var row in _view?.SourceCollection.Cast<FileItem>() ?? [])
            {
                if (row.IsDirectory)
                    row.ContainsNotes = NotesStore.FolderHasNotes(row.FullPath);
            }
            void Walk(FolderNode node)
            {
                node.RefreshMark();
                foreach (var child in node.Children)
                    Walk(child);
            }
            foreach (var root in _treeRoots)
                Walk(root);
            RebuildTagsBranch();
        });
    }

    bool _notesChangedQueued;
    void DarkToggle_Click(object sender, RoutedEventArgs e) => SetDarkMode(!ThemeManager.IsDark);
    void DarkModeMenu_Click(object sender, RoutedEventArgs e) => SetDarkMode(DarkModeMenu.IsChecked);

    /// <summary>Switches every open window between the light and dark color sets (saved on exit).</summary>
    void SetDarkMode(bool dark)
    {
        ThemeManager.Apply(dark);
        DarkModeMenu.IsChecked = dark;
        DarkGlyph.Text = dark ? "" : "";   // sun = back to light, moon = go dark
        DarkToggle.ToolTip = dark ? "라이트 모드로 전환" : "다크 모드로 전환";
        if (_saveTimer != null)
            ScheduleSave();   // remembered right away, so the next start opens in the same mode
    }
    void InfoPaneToggle_Click(object sender, RoutedEventArgs e) => SetNotePaneVisible(InfoPaneToggle.IsChecked == true);

    void ColumnsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColumnsButton, Placement = PlacementMode.Bottom, FontFamily = FontFamily, FontSize = FontSize };   // not the button's icon font
        FillColumnMenu(menu.Items);
        menu.IsOpen = true;
    }
    void NotePaneMenu_Click(object sender, RoutedEventArgs e) => SetNotePaneVisible(NotePaneMenu.IsChecked);

    void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Shift+right-click: straight to the Windows menu (with extended items). On empty space it is the
        // folder's background menu (새로 만들기, 붙여넣기 …), as in Explorer.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            bool onItem = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FileList, source) is ListViewItem;
            var point = ShellContextMenu.CursorPosition();
            if (onItem && SelectedItems.Count > 0)
                ShowWindowsMenu(SelectedItems.Select(i => i.FullPath).ToList(), point, extended: true);
            else
                ShowWindowsBackgroundMenu(point, extended: true);
            return;
        }
        var items = SelectedItems;
        bool any = items.Count > 0;
        OpenMenuItem.IsEnabled = any;
        // In 내 PC the rows are drives and the user's main folders: open, browse and inspect only.
        bool editable = !InThisPc;
        RenameMenuItem.IsEnabled = editable && items.Count == 1;
        OpenInNewTabMenuItem.IsEnabled = items.Any(i => i.IsDirectory);
        CopyMenuItem.IsEnabled = CutMenuItem.IsEnabled = DeleteMenuItem.IsEnabled = editable && any;
        PasteMenuItem.IsEnabled = !InVirtual && Clipboard.ContainsFileDropList();
        NewFolderMenuItem.IsEnabled = !InVirtual;
        BatchRenameMenuItem.IsEnabled = editable && items.Count >= 2;
        var dual = DualPane.Visibility == Visibility.Visible && any ? Visibility.Visible : Visibility.Collapsed;
        CopyToDualMenuItem.Visibility = MoveToDualMenuItem.Visibility = dual;
        string dualName = DualPane.Folder.Length > 0 ? $" ({FileSystem.DisplayName(DualPane.Folder)})" : "";
        CopyToDualMenuItem.Header = "오른쪽 창으로 복사" + dualName;
        MoveToDualMenuItem.Header = "오른쪽 창으로 이동" + dualName;
        MoveToDualMenuItem.IsEnabled = editable;
        ZipMenuItem.IsEnabled = editable && any;
        UnzipMenuItem.Visibility = items is [{ IsDirectory: false } zip] && zip.Extension == "zip" ? Visibility.Visible : Visibility.Collapsed;
        OpenLocationMenuItem.Visibility = CurrentSearch != null && items.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        PropertiesMenuItem.IsEnabled = any;
        ShowInExplorerMenuItem.Header = any ? "Windows 탐색기에서 보기" : "현재 폴더를 Windows 탐색기에서 열기";
        CopyPathMenuItem.Header = any ? "경로 복사" : "현재 폴더 경로 복사";
        string folder = FileContextFolder;
        ConfigureFavoriteMenuItem(FileFavoriteMenuItem, folder, folder == _active.FullPath ? "현재 폴더를 " : "");
        if (FileSystem.IsVirtual(folder))
            FileFavoriteMenuItem.IsEnabled = false;
    }

    void FileOpen_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void FileOpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SelectedItems.Where(i => i.IsDirectory))
            NewTab(item.FullPath);
    }

    void FileShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems.FirstOrDefault() is FileItem item)
            OpenInWindowsExplorer(item.FullPath, select: true);
        else
            OpenInWindowsExplorer(_active.FullPath, select: false);
    }

    void FileCopy_Click(object sender, RoutedEventArgs e) => ClipboardSelected("copy");
    void FileCut_Click(object sender, RoutedEventArgs e) => ClipboardSelected("cut");
    void FilePaste_Click(object sender, RoutedEventArgs e) => PasteIntoCurrentFolder();
    void FileCopyPath_Click(object sender, RoutedEventArgs e) => CopyPathsToClipboard();

    void FileProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems.FirstOrDefault() is FileItem item)
            Native.ShowProperties(item.FullPath);
    }

    void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key })
            SortBy(key);
    }

    /// <summary>Sorts the main list and the second pane the same way (so they line up for comparing).</summary>
    void SortBy(string key)
    {
        if (!ColumnLabels.ContainsKey(key))
            return;
        if (key == _sortKey)
            _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            // Sizes and dates are most useful largest/newest first.
            _sortDescending = key is "Size" or "Modified" or "Created";
        }
        UpdateSortHeaders();
        if (_view != null)
            _view.CustomSort = new FileItemComparer(_sortKey, _sortDescending);
        DualPane.SetSort(_sortKey, _sortDescending);
        SyncDualColumns();
    }

    void UpdateSortHeaders()
    {
        // All columns, including hidden ones, so a column shown later already has its title.
        foreach (var column in AllColumns)
        {
            if (column.Header is GridViewColumnHeader { Tag: string key } header && ColumnLabels.TryGetValue(key, out var label))
                header.Content = key == _sortKey ? label + (_sortDescending ? "  ▼" : "  ▲") : label;
        }
    }

    // ───────────── Column chooser ─────────────

    static readonly HashSet<string> HiddenByDefault = new() { "Accessed", "Attributes", "Folder", "Compare" };

    List<GridViewColumn>? _allColumns;

    GridView FileGridView => (GridView)FileList.View;

    /// <summary>Every column defined in XAML, in default order (captured before any are hidden).</summary>
    List<GridViewColumn> AllColumns => _allColumns ??= FileGridView.Columns.ToList();

    static string ColumnKey(GridViewColumn column) => (column.Header as GridViewColumnHeader)?.Tag as string ?? "";

    static string ColumnTitle(string key) => key == "Index" ? "번호 (#)" : ColumnLabels.GetValueOrDefault(key, key);

    /// <summary>Shows columns in the saved order and widths; null restores the defaults.</summary>
    void ApplyColumnSettings(List<ColumnSetting>? saved)
    {
        var all = AllColumns;
        FileGridView.Columns.Clear();
        if (saved == null)
        {
            foreach (var column in all.Where(c => !HiddenByDefault.Contains(ColumnKey(c))))
                FileGridView.Columns.Add(column);
            return;
        }
        foreach (var setting in saved)
        {
            var column = all.FirstOrDefault(c => ColumnKey(c) == setting.Key);
            if (column == null)
                continue;
            if (setting.Width > 10)
                column.Width = setting.Width;
            if (setting.Visible && !FileGridView.Columns.Contains(column))
                FileGridView.Columns.Add(column);
        }
        // Columns added in a newer version than the saved settings.
        foreach (var column in all.Where(c => saved.All(s => s.Key != ColumnKey(c)) && !HiddenByDefault.Contains(ColumnKey(c))))
            SetColumnVisible(ColumnKey(column), true);
        if (!FileGridView.Columns.Any(c => ColumnKey(c) == "Name"))
            SetColumnVisible("Name", true);
    }

    void SetColumnVisible(string key, bool visible)
    {
        var column = AllColumns.FirstOrDefault(c => ColumnKey(c) == key);
        if (column == null || visible == FileGridView.Columns.Contains(column))
            return;
        if (!visible)
        {
            FileGridView.Columns.Remove(column);
            return;
        }
        // Put it back after the nearest column that precedes it in the default order (keeps user reordering).
        int defaultIndex = AllColumns.IndexOf(column);
        var before = AllColumns.Take(defaultIndex).LastOrDefault(c => FileGridView.Columns.Contains(c));
        FileGridView.Columns.Insert(before == null ? 0 : FileGridView.Columns.IndexOf(before) + 1, column);
    }

    List<ColumnSetting> CurrentColumnSettings() =>
            // Columns shown only for a purpose (위치 in results, 비교 while comparing) are not the user's choice: saved as hidden.
        FileGridView.Columns.Select(c => new ColumnSetting(ColumnKey(c), !_autoColumns.Contains(ColumnKey(c)),
                double.IsNaN(c.Width) ? c.ActualWidth : c.Width))
            .Concat(AllColumns.Where(c => !FileGridView.Columns.Contains(c))
                .Select(c => new ColumnSetting(ColumnKey(c), false, double.IsNaN(c.Width) ? 100 : c.Width)))
            .ToList();

    /// <summary>One checkable entry per column (이름 is always shown), plus "reset".</summary>
    void FillColumnMenu(ItemCollection items)
    {
        items.Clear();
        foreach (var column in AllColumns)
        {
            string key = ColumnKey(column);
            var item = new MenuItem
            {
                Header = ColumnTitle(key),
                IsCheckable = true,
                IsChecked = FileGridView.Columns.Contains(column),
                IsEnabled = key != "Name",
                StaysOpenOnClick = true,
            };
            // Checked/Unchecked rather than Click, so keyboard and accessibility toggles work too.
            item.Checked += (_, _) => { SetColumnVisible(key, true); SyncDualColumns(); };
            item.Unchecked += (_, _) => { SetColumnVisible(key, false); SyncDualColumns(); };
            items.Add(item);
        }
        items.Add(new Separator());
        var reset = new MenuItem { Header = "기본 열로 되돌리기" };
        reset.Click += (_, _) => { ApplyColumnSettings(null); SyncDualColumns(); };
        items.Add(reset);
        items.Add(new MenuItem
        {
            Header = "열 머리글을 끌어서 순서를, 경계선을 끌어서 너비를 바꿀 수 있습니다",
            IsEnabled = false,
        });
    }

    void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            FillColumnMenu(menu.Items);
    }

    void ColumnsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == ColumnsMenu)
            FillColumnMenu(ColumnsMenu.Items);
    }

    // ───────────── List filter ─────────────

    /// <summary>The main list's filter: 비교 "다른 것만 보기".</summary>
    bool FilterItem(object obj) => !CompareHides((FileItem)obj);

    /// <summary>The right list's filter (the same rule).</summary>
    bool PaneFilter(object obj) => !CompareHides((FileItem)obj);

    // ───────────── Address bar & breadcrumb ─────────────

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AddressBox.Text = FileSystem.IsVirtual(_active.FullPath) ? FileSystem.DisplayName(_active.FullPath) : _active.FullPath;
            FileList.Focus();
            return;
        }
        if (e.Key != Key.Enter)
            return;

        string text = Environment.ExpandEnvironmentVariables(AddressBox.Text.Trim().Trim('"'));
        if (text.Length == 0)
            return;
        if (FileSystem.IsThisPc(text))
        {
            Navigate(FileSystem.ThisPc);
            FileList.Focus();
            return;
        }
        if (InThisPc && !Path.IsPathRooted(text))
        {
            // A name typed while in 내 PC ("문서", "Windows (C:)") means that entry.
            var match = _view?.Cast<FileItem>().FirstOrDefault(i => i.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase));
            if (match != null)
                Navigate(match.FullPath);
            FileList.Focus();
            return;
        }
        try
        {
            if (!Path.IsPathRooted(text))
                text = Path.Combine(_active.FullPath, text);
        }
        catch { }

        if (File.Exists(text))
            Go(Path.GetDirectoryName(text)!, Path.GetFileName(text));
        else
            Go(text);
        FileList.Focus();
    }

    void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The first click only focuses (selecting everything); otherwise the click would immediately
        // move the caret to the click point and undo the selection. Later clicks place the caret normally.
        if (AddressBox.IsKeyboardFocusWithin)
            return;
        AddressBox.Focus();
        e.Handled = true;
    }

    void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            Go(path);
    }

    // ───────────── Status ─────────────

    void UpdateStatus()
    {
        if (_view == null)
            return;
        int folders = 0, files = 0;
        foreach (FileItem item in _view)
        {
            if (item.IsDirectory) folders++;
            else files++;
        }
        string text = $"{folders + files:N0}개 항목 (폴더 {folders:N0}, 파일 {files:N0})";
        if (_statusNote != null && DateTime.Now < _statusNoteUntil)
            text = _statusNote + "   |   " + text;
        var selected = SelectedItems;
        if (selected.Count > 0)
        {
            long size = selected.Sum(i => i.Size ?? 0);
            text += $"   |   {selected.Count:N0}개 선택됨";
            if (size > 0)
                text += $" ({FileSystem.FormatBytes(size)})";
        }
        StatusText.Text = text;
    }

    string? _statusNote;
    DateTime _statusNoteUntil;

    /// <summary>A message that stays at the front of the status bar for a few seconds, even if the list reloads meanwhile.</summary>
    void ShowStatusNote(string note)
    {
        _statusNote = note;
        _statusNoteUntil = DateTime.Now.AddSeconds(5);
        UpdateStatus();
    }

    void UpdateDriveText(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            DriveText.Text = drive.IsReady
                ? $"{drive.Name.TrimEnd('\\')} 여유 공간 {FileSystem.FormatBytes(drive.AvailableFreeSpace)} / {FileSystem.FormatBytes(drive.TotalSize)}"
                : "";
        }
        catch
        {
            DriveText.Text = "";
        }
    }

    // ───────────── Window-level commands ─────────────

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None;
        bool ctrl = mods == ModifierKeys.Control;
        bool alt = mods == ModifierKeys.Alt;
        bool shift = mods == ModifierKeys.Shift;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        bool altShift = mods == (ModifierKeys.Alt | ModifierKeys.Shift);
        bool typing = IsTypingInTextBox;
        // The second pane handles its own list keys (Backspace, Del, F2, F5, Ctrl+A/C/X/V, Alt+↑).
        if (DualPane.HasFocus && (key is Key.Back or Key.Delete or Key.F2 or Key.F5 || ctrl && key is Key.A or Key.C or Key.X or Key.V ||
                                  alt && key == Key.Up))
            return;
        bool inList = FileList.IsKeyboardFocusWithin && !typing;

        // ── Windows Explorer shortcuts ──
        // Navigation
        if (alt && key == Key.Left) GoBack();
        else if (alt && key == Key.Right) GoForward();
        else if (alt && key == Key.Up) GoUp();
        else if (none && key == Key.Back && !typing) GoBack();                       // Windows 10/11: Backspace = back
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || (none && key == Key.F4))
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
        else if ((ctrl && key is Key.E or Key.F) || (none && key == Key.F3))
        {
            OpenSearch();
        }
        else if ((none && key == Key.F5) || (ctrl && key == Key.R)) Refresh();
        else if ((none || shift) && key == Key.F6) CyclePaneFocus(forward: none);
        else if (none && key == Key.F11) ToggleFullScreen();
        // Files
        else if (ctrl && key == Key.A && !typing)
        {
            FileList.Focus();
            FileList.SelectAll();
        }
        else if (FolderTree.IsKeyboardFocusWithin && !typing && HandleTreeKey(key, mods)) { }
        else if (ctrl && key is Key.C or Key.X or Key.V && HandleFileClipboardKey(key)) { }
        else if (inList && ((key == Key.Delete && (none || shift)) || (ctrl && key == Key.D)))
            DeleteSelected(permanently: shift);
        else if (inList && ctrl && key == Key.Enter)
        {
            foreach (var item in SelectedItems.Where(i => i.IsDirectory))
                NewTab(item.FullPath);
        }
        else if (ctrlShift && key == Key.N) NewFolder();
        // Windows and tabs
        else if (ctrl && key == Key.N) OpenNewWindow();
        else if (ctrl && key == Key.T) NewTab(_active.FullPath);
        else if (ctrl && key == Key.W) CloseTab(_active);
        else if (ctrl && key == Key.Tab) CycleTab(1);
        else if (ctrlShift && key == Key.Tab) CycleTab(-1);
        // Panes
        else if (alt && key == Key.P) ToggleInfoTab(PreviewTab);                     // preview pane
        else if (altShift && key == Key.P) ToggleInfoTab(PropsTab);                  // details pane
        else if (ctrlShift && key == Key.P) Navigate(FileSystem.ThisPc);
        else if (ctrlShift && key == Key.H) SetNoteMarks(!ShowNoteMarks);
        else if (ctrl && key == Key.Z && !typing) UndoLast();
        else if (ctrlShift && key == Key.D) SetDualPane(DualPane.Visibility != Visibility.Visible);
        else if (ctrlShift && key == Key.K) SetCompare(!_compareOn);
        else if (ctrlShift && key == Key.E)                                          // expand tree to current folder
        {
            SyncTree(_active.FullPath);
            FolderTree.Focus();
        }
        else if (none && key == Key.F1) ShowShortcutHelp();
        // ── FileStar's own ──
        else if (ctrl && key == Key.H) SetShowHidden(!FileSystem.ShowHidden);
        else if (ctrl && key == Key.B) ToggleFavorite(ActiveFolder);
        else if (ctrl && key == Key.M) EditNote();
        else if (ctrlShift && key == Key.M) RunAiSummary();
        else if (none && key == Key.F9) SetNotePaneVisible(NotePane.Visibility != Visibility.Visible);
        else if (ctrl && key == Key.S && _editingNote) SaveNoteEdit();
        else if (mods == ModifierKeys.None && key == Key.Escape && NoteBox.IsKeyboardFocusWithin)
        {
            ResolvePendingNoteEdit();
            FileList.Focus();
        }
        else if (ctrl && key is >= Key.D1 and <= Key.D9 && !(Keyboard.FocusedElement is TextBox)) OpenFavorite(key - Key.D1);
        else return;
        e.Handled = true;
    }

    // ── Explorer shortcut helpers ──

    /// <summary>F6 / Shift+F6: address bar → folder tree → file list → info pane → …, like Explorer.</summary>
    void CyclePaneFocus(bool forward)
    {
        var panes = new List<FrameworkElement> { AddressBox, FolderTree, FileList };
        if (NotePane.Visibility == Visibility.Visible)
            panes.Add(InfoTabs);
        int current = panes.FindIndex(p => p.IsKeyboardFocusWithin);
        var next = panes[((current < 0 ? (forward ? -1 : 0) : current) + (forward ? 1 : panes.Count - 1)) % panes.Count];
        if (next == FileList && FileList.SelectedItem != null &&
            FileList.ItemContainerGenerator.ContainerFromItem(FileList.SelectedItem) is ListViewItem selectedRow)
            selectedRow.Focus();
        else if (next == FolderTree && _selectedNode != null)
        {
            FolderTree.Focus();
            BringTreeChainIntoView(_pathChain);
        }
        else
            next.Focus();
    }

    WindowState _stateBeforeFullScreen;
    bool _fullScreen;

    /// <summary>F11: full screen without title bar, and back.</summary>
    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // re-maximizing after the style change covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
        FullScreenGlyph.Text = _fullScreen ? "" : "";   // shrink while full screen, expand otherwise
        FullScreenButton.ToolTip = _fullScreen ? "전체 화면 끝내기 (F11)" : "전체 화면 (F11)";
    }

    /// <summary>Alt+P (preview) / Alt+Shift+P (details): show the info pane on that tab, or hide it if already shown.</summary>
    void ToggleInfoTab(TabItem tab)
    {
        if (NotePane.Visibility == Visibility.Visible && InfoTabs.SelectedItem == tab)
            SetNotePaneVisible(false);
        else
        {
            SetNotePaneVisible(true);
            InfoTabs.SelectedItem = tab;
        }
    }

    /// <summary>Ctrl+Shift+N, the toolbar button, the menus: a new folder in the list being used.</summary>
    void NewFolder()
    {
        if (DualActive)
            DualPane.NewFolder();
        else
            _ = CreateNewFolderAsync();
    }

    /// <summary>"새 폴더" (or "새 폴더 (2)" …) in the current folder, or in <paramref name="parent"/> (the tree's menu,
    /// which goes there first), then rename it in place.</summary>
    async Task CreateNewFolderAsync(string? parent = null)
    {
        if (parent != null && !string.Equals(parent, _active.FullPath, StringComparison.OrdinalIgnoreCase))
            Navigate(parent);
        if (InVirtual)
            return;
        string name = FileSystem.NewFolderName(_active.FullPath);
        try
        {
            Directory.CreateDirectory(Path.Combine(_active.FullPath, name));
            UndoStack.Push(new CreateUndo(new[] { Path.Combine(_active.FullPath, name) }, "새 폴더"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "새 폴더를 만들 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await LoadFilesAsync(_active.FullPath, new[] { name });
        if (_view?.SourceCollection.Cast<FileItem>().FirstOrDefault(i => i.Name == name) is FileItem created)
            BeginRename(created);
    }

    /// <summary>Ctrl+N: another FileStar window on the current folder.</summary>
    void OpenNewWindow()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"\"{_active.FullPath}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "새 창을 열 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void NewWindow_Click(object sender, RoutedEventArgs e) => OpenNewWindow();
    void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolder();
    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    void ShortcutHelp_Click(object sender, RoutedEventArgs e) => ShowShortcutHelp();

    void ShowShortcutHelp() => MessageBox.Show(this, """
        [이동]
        Alt+← / Backspace   뒤로              Alt+→   앞으로
        Alt+↑               상위 폴더        Ctrl+Shift+P   내 PC
        Ctrl+L / Alt+D / F4  주소창          F6 / Shift+F6   영역 전환
        Ctrl+F / Ctrl+E / F3  검색 탭 (이름·확장자·태그·메모에서 찾기)
        F5 / Ctrl+R         새로 고침         Ctrl+Z        실행 취소 (이름·이동·복사·삭제·새 폴더)
        F11                 전체 화면

        [파일]
        Enter               열기             Ctrl+Enter   새 탭에서 열기
        F2                  이름 바꾸기       Ctrl+Shift+N  새 폴더
        Del / Ctrl+D        휴지통으로 삭제   Shift+Del     영구 삭제
        Ctrl+C / X / V      복사 / 잘라내기 / 붙여넣기
        Ctrl+Shift+C        경로 복사         Ctrl+A        모두 선택
        Alt+Enter           속성             Shift+F10     오른쪽 클릭 메뉴
        글자 입력            그 글자로 시작하는 파일로 이동
        이름 바꾸는 중 ↑/↓ (Tab)   저장하고 이전/다음 파일 이름 바꾸기
        왼쪽 트리에서도      Ctrl+C / X / V, Del, F2 (선택한 폴더에 적용)

        [창 · 탭]
        Ctrl+N  새 창    Ctrl+T  새 탭    Ctrl+W  탭 닫기    Ctrl+Tab  다음 탭

        [창 영역]
        Alt+P  미리보기    Alt+Shift+P  속성    Ctrl+Shift+E  트리를 현재 폴더까지 펼치기
        Ctrl+Shift+D  두 창 보기 — 주소창·뒤로/앞으로·트리·필터·정보 창은 파란 줄이 있는(활성) 창을 따릅니다
        Ctrl+Shift+K  폴더 비교 (이름·크기·수정 날짜로 비교, 다른 것만 보기, 한쪽으로 복사)

        [FileStar 기능]
        Ctrl+B  즐겨찾기 추가/제거    Ctrl+1~9  즐겨찾기 열기
        Ctrl+M  메모 수정 (Ctrl+S 저장 · Esc 취소)    Ctrl+Shift+M  AI로 메모·태그    F9  정보 창
        Ctrl+H  숨김 파일 표시    Ctrl+Shift+H  메모·태그 표시 켜기/끄기
        Shift+오른쪽 클릭  탐색기 메뉴 바로 열기
        """, AppPaths.AppName + " 단축키 (F1)", MessageBoxButton.OK, MessageBoxImage.Information);

    void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Mouse side buttons
        if (e.ChangedButton == MouseButton.XButton1) GoBack();
        else if (e.ChangedButton == MouseButton.XButton2) GoForward();
    }

    void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DualActive)
            DualPane.Reload();
        else
            Refresh();
    }
    void ShowHidden_Click(object sender, RoutedEventArgs e) => SetShowHidden(ShowHiddenMenu.IsChecked);
    void OpenCurrentInExplorer_Click(object sender, RoutedEventArgs e) => OpenInWindowsExplorer(_active.FullPath, select: false);
    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    void ThisPc_Click(object sender, RoutedEventArgs e) => Navigate(FileSystem.ThisPc);

    // ── 최근 폴더 / 최근 파일 ──

    void RecentMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem menu || (menu != RecentFoldersMenu && menu != RecentFilesMenu))
            return;
        bool folders = menu == RecentFoldersMenu;
        var paths = (folders ? RecentItems.Folders : RecentItems.Files)
            .Where(p => folders ? Directory.Exists(p) : File.Exists(p))   // skip what was moved or deleted since
            .ToList();
        menu.Items.Clear();
        foreach (var path in paths)
        {
            var item = new MenuItem
            {
                Header = (folders ? FileSystem.DisplayName(path) : Path.GetFileName(path)).Replace("_", "__"),   // no access keys
                InputGestureText = folders ? Path.GetDirectoryName(path) ?? "" : Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
                ToolTip = path,
                Icon = new Image { Source = ShellInfo.GetIcon(path, folders), Width = 16, Height = 16 },
            };
            item.Click += (_, _) =>
            {
                if (folders)
                    Go(path);
                else
                    ShellOpen(path);
            };
            menu.Items.Add(item);
        }
        if (paths.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(없음)", IsEnabled = false });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "최근 목록 지우기", IsEnabled = paths.Count > 0 };
        clear.Click += (_, _) =>
        {
            RecentItems.Clear();
            ScheduleSave();
        };
        menu.Items.Add(clear);
    }

    // ── 이름 한꺼번에 바꾸기 ──

    void BatchRename_Click(object sender, RoutedEventArgs e)
    {
        // In list order, so numbering follows what the user sees.
        var items = _view?.Cast<FileItem>().Where(i => FileList.SelectedItems.Contains(i)).ToList() ?? new();
        if (items.Count < 2 || InThisPc)
            return;
        var dialog = new BatchRenameWindow(items) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Plan.Count == 0)
            return;
        var done = new List<UndoAction>();
        var failed = new List<string>();
        // Two steps through temporary names, so swaps like a→b, b→a don't trip over each other.
        var staged = new List<(string Old, string Temp, string Final)>();
        foreach (var (path, newName) in dialog.Plan)
        {
            string folder = Path.GetDirectoryName(path)!;
            string temp = Path.Combine(folder, $"~filestar-{Guid.NewGuid():N}");
            try
            {
                MovePath(path, temp);
                staged.Add((path, temp, Path.Combine(folder, newName)));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        foreach (var (old, temp, final) in staged)
        {
            try
            {
                MovePath(temp, final);
                NotesStore.Rename(old, final);
                done.Add(new RenameUndo(old, final));
            }
            catch (Exception ex)
            {
                MovePath(temp, old);   // put it back as it was
                failed.Add($"{Path.GetFileName(old)}: {ex.Message}");
            }
        }
        if (done.Count > 0)
            UndoStack.Push(new GroupUndo(done, $"이름 한꺼번에 바꾸기 ({done.Count:N0}개)"));
        _ = LoadFilesAsync(_active.FullPath, dialog.Plan.Select(p => p.NewName).ToList());
        ShowStatusNote($"이름 {done.Count:N0}개를 바꿨습니다 (Ctrl+Z로 되돌리기)");
        if (failed.Count > 0)
            MessageBox.Show(this, "바꾸지 못한 항목:\n" + string.Join("\n", failed.Take(15)), AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    // ── zip ──

    async void Zip_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems;
        if (items.Count == 0 || InThisPc)
            return;
        string folder = items[0].FolderPath;
        string baseName = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : new DirectoryInfo(folder).Name;
        string zipPath = ZipTools.FreeName(folder, baseName, ".zip");
        var paths = items.Select(i => i.FullPath).ToList();
        StatusText.Text = $"'{Path.GetFileName(zipPath)}' 만드는 중...";
        try
        {
            await Task.Run(() => ZipTools.Create(zipPath, paths, CancellationToken.None));
        }
        catch (Exception ex)
        {
            try { File.Delete(zipPath); } catch { }
            MessageBox.Show(this, "압축하지 못했습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UndoStack.Push(new CreateUndo(new[] { zipPath }, "압축"));
        if (!InVirtual)
            await LoadFilesAsync(_active.FullPath, new[] { Path.GetFileName(zipPath) });
        ShowStatusNote($"'{Path.GetFileName(zipPath)}'로 압축했습니다");
    }

    async void Unzip_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems is not [{ IsDirectory: false } zip] || zip.Extension != "zip")
            return;
        string target = ZipTools.FreeName(zip.FolderPath, Path.GetFileNameWithoutExtension(zip.Name), "");
        StatusText.Text = $"'{zip.Name}' 푸는 중...";
        try
        {
            await Task.Run(() => ZipTools.Extract(zip.FullPath, target));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "압축을 풀지 못했습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UndoStack.Push(new CreateUndo(new[] { target }, "압축 풀기"));
        if (!InVirtual)
            await LoadFilesAsync(_active.FullPath, new[] { Path.GetFileName(target) });
        ShowStatusNote($"'{Path.GetFileName(target)}' 폴더에 풀었습니다");
    }

    // ── Undo (Ctrl+Z) ──

    void Undo_Click(object sender, RoutedEventArgs e) => UndoLast();

    void UpdateUndoMenu()
    {
        var next = UndoStack.Peek;
        UndoMenuItem.IsEnabled = next != null;
        UndoMenuItem.Header = next == null ? "실행 취소" : "실행 취소: " + next.Description;
    }

    async void UndoLast()
    {
        if (UndoStack.Peek == null)
        {
            StatusText.Text = "되돌릴 작업이 없습니다.";
            return;
        }
        ResolvePendingNoteEdit();
        var (action, problem) = await UndoStack.UndoAsync(WindowHandle);
        if (action == null)
            return;
        // The folder being shown may itself have been renamed back or removed: step out to what still exists.
        if (!InVirtual && !Directory.Exists(_active.FullPath))
        {
            string? folder = _active.FullPath;
            while (folder != null && !Directory.Exists(folder))
                folder = Path.GetDirectoryName(folder);
            if (folder != null)
                Navigate(folder);
        }
        // Folder names may have changed beside the current one; reloading must not move the view.
        _quietTreeSelection = true;
        try
        {
            _selectedNode?.Parent?.Reload();
        }
        finally
        {
            _quietTreeSelection = false;
        }
        _selectedNode?.Reload();
        SyncTree(_active.FullPath);
        UpdateDriveText(_active.FullPath);
        await LoadFilesAsync(_active.FullPath);
        ShowStatusNote("되돌림: " + action.Description + (problem != null ? " · " + problem : ""));
    }

    static string? SpecialPath(object? tag)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return tag switch
        {
            "ThisPc" => FileSystem.ThisPc,
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads" => Path.Combine(home, "Downloads"),
            "Home" => home,
            _ => null,
        };
    }

    void GoSpecial_Click(object sender, RoutedEventArgs e)
    {
        if (SpecialPath((sender as MenuItem)?.Tag) is string path)
            Go(path);
    }

    /// <summary>내 PC, 바탕 화면, 문서 … in the 이동 menu show the same icons Windows uses for them.</summary>
    void SetGoMenuIcons()
    {
        foreach (var item in GoMenu.Items.OfType<MenuItem>())
        {
            if (SpecialPath(item.Tag) is not string path)
                continue;
            var icon = FileSystem.IsThisPc(path) ? ShellInfo.ThisPcIcon
                : Directory.Exists(path) ? ShellInfo.GetIcon(path, true, exact: true) : null;
            if (icon != null)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        }
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _watcher?.Dispose();
        FavoritesStore.Save(notify: false);   // keeps group expand/collapse state
        ResolvePendingNoteEdit();
        CommitNote();
        PreviewHandlerHost.Unload();
        _saveTimer.Stop();
        SaveSettings();
        // The last window: a downloaded update is installed once FileStar has exited.
        if (Application.Current.Windows.OfType<MainWindow>().Count(w => w != this) == 0)
            Updater.ApplyOnExit();
    }

    readonly DispatcherTimer _saveTimer;

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the whole window state: tabs and their folders, layout, columns, panes, theme.</summary>
    void SaveSettings()
    {
        if (_active == null)
            return;   // still starting up
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
            return;
        new AppSettings
        {
            Tabs = _tabs.Select(t => t.FullPath).ToList(),
            ActiveTab = _tabs.IndexOf(_active),
            PinnedTabs = _tabs.Select((t, i) => t.IsPinned ? i : -1).Where(i => i >= 0).ToList(),
            Left = bounds.Left,
            Top = bounds.Top,
            InfoTab = InfoTabs.SelectedIndex,
            ShowNoteMarks = ShowNoteMarks,
            SearchOptions = SearchLocation.OptionLetters(_searchFields, _searchSubfolders),
            DualPaneVisible = DualPane.Visibility == Visibility.Visible,
            DualPanePath = DualPane.Folder.Length > 0 ? DualPane.Folder : null,
            DualPaneRatio = DualPane.Visibility == Visibility.Visible ? DualRatioNow() : _dualRatio,
            RecentFolders = RecentItems.Folders.ToList(),
            RecentFiles = RecentItems.Files.ToList(),
            PropLabelWidth = _propLabelWidth,
            HiddenProps = _hiddenProps.OrderBy(p => p).ToList(),
            ShowHidden = FileSystem.ShowHidden,
            TreeWidth = TreeColumn.ActualWidth,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            SortKey = _sortKey,
            SortDescending = _sortDescending,
            NotePaneVisible = NotePane.Visibility == Visibility.Visible,
            NotePaneWidth = NotePane.Visibility == Visibility.Visible ? NoteColumn.ActualWidth : _notePaneWidth,
            Columns = CurrentColumnSettings(),
            DarkMode = ThemeManager.IsDark,
        }.Save();
    }
}
