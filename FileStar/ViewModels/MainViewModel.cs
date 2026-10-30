using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>The header every info-pane tab starts with: the item (or folder) being described.</summary>
public sealed record InfoHeader(ImageSource? Icon, string Title, string Subtitle);

/// <summary>Where the view should put the keyboard focus.</summary>
/// <summary>Where the view should move the keyboard focus. TreeIfFocused: back to the tree, unless the user already
/// clicked somewhere else.</summary>
public enum FocusTarget { FileList, Tree, TreeIfFocused, SearchBox, NoteBox }

/// <summary>
/// The main window: tabs and navigation, the address bar and breadcrumb, which list is active (두 창 보기),
/// the info pane, and every command the menus, toolbar and keyboard offer. Panels with state of their own
/// (list, tree, memo, properties, search, compare, status) are child ViewModels; the partial files hold the
/// commands by area.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    const string AppName = AppPaths.AppName;

    readonly AppServices _s;
    readonly UndoContext _undoContext;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly IReadOnlyList<string> _startPaths;
    IDisposable? _watcher;
    BrowserTab _active = null!;
    string _title = AppName, _addressText = "";
    IReadOnlyList<Crumb> _crumbs = Array.Empty<Crumb>();
    InfoHeader _infoHeader = new(null, "", "");
    bool _isDualVisible, _isDualActive, _showNoteMarks = true, _listNoteMarks = true, _isInfoPaneVisible;
    int _infoTabIndex;
    bool _notesChangedQueued, _reloadAfterRename;

    public MainViewModel(AppServices services, IReadOnlyList<string> startPaths)
    {
        _s = services;
        _startPaths = startPaths;
        _undoContext = new UndoContext(services.Notes, services.FileOps);
        Settings = services.Settings.Load();

        FileList = new FileListViewModel(services.FileSystem, services.Notes);
        Dual = new FolderPaneViewModel(services.FileSystem, services.Notes, services.FileOps, services.Dialogs, services.Shell, services.Watch);
        Tree = new FolderTreeViewModel(new FolderTreeContext(services.FileSystem, services.Notes), services.Favorites, services.Notes);
        Notes = new NotePaneViewModel(services.Notes, services.Dialogs);
        Properties = new PropertiesViewModel();
        Search = new SearchPanelViewModel();
        Compare = new CompareViewModel();
        Status = new StatusBarViewModel();

        Wire();
        CreateCommands();
        ApplySettings(Settings);
    }

    // ── Child ViewModels ──

    public FileListViewModel FileList { get; }
    public FolderPaneViewModel Dual { get; }
    public FolderTreeViewModel Tree { get; }
    public NotePaneViewModel Notes { get; }
    public PropertiesViewModel Properties { get; }
    public SearchPanelViewModel Search { get; }
    public CompareViewModel Compare { get; }
    public StatusBarViewModel Status { get; }

    /// <summary>The settings read at start-up; the view applies the window layout from them.</summary>
    public AppSettings Settings { get; }

    // ── Requests to the view ──

    public event Action<FocusTarget>? FocusRequested;

    /// <summary>The selection or tab changed: the 속성 and 미리보기 tabs should catch up once it settles.</summary>
    public event Action? DetailsRequested;

    /// <summary>Something worth saving changed; the view saves shortly after (with its layout).</summary>
    public event Action? SaveRequested;

    /// <summary>The sort column changed: the view redraws the column headers.</summary>
    public event Action? SortChanged;

    void RequestSave() => SaveRequested?.Invoke();

    void Focus(FocusTarget target) => FocusRequested?.Invoke(target);

    void Wire()
    {
        FileList.SelectionChanged += OnMainSelectionChanged;
        FileList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileListViewModel.LoadingMessage))
                Status.Update(FileList);
        };
        FileList.SetFilter(Compare.Hides);
        Dual.List.SetFilter(Compare.Hides);
        Compare.FilterChanged += () =>
        {
            FileList.RefreshFilter();
            Dual.List.RefreshFilter();
            Status.Update(FileList);
        };

        Dual.MainFolder = () => MainFolder;
        Dual.Navigated += () =>
        {
            if (IsDualActive)
                UpdateChrome();
            RecomputeCompare();
            RequestSave();
        };
        Dual.List.SelectionChanged += () =>
        {
            if (!IsDualActive)
                return;
            UpdateInfoPane();
            UpdateTitle();
        };
        Dual.Reloaded += RecomputeCompare;
        Dual.FilesChanged += () => Refresh(reloadTree: false);
        Dual.CloseRequested += () => SetDualPane(false);

        Tree.NavigateRequested += path => Go(path);
        Notes.Saved += (path, note) =>
        {
            FileList.UpdateNote(path, note);
            Dual.List.UpdateNote(path, note);
        };
        Notes.TagOpened += tag => Navigate(SearchLocation.Tag(tag).Path);
        Search.SearchRequested += RunSearch;
        Search.Problem += message => Status.Note(message, FileList);
        Properties.SettingsChanged += RequestSave;

        _s.Notes.Changed += OnNotesChanged;
        _s.Favorites.Changed += OnFavoritesChanged;
        _s.Undo.Changed += () => Defer(() =>
        {
            OnPropertyChanged(nameof(UndoHeader));
            CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>Runs <paramref name="action"/> after the current UI work (or right away without a UI thread, as in tests).</summary>
    void Defer(Action action)
    {
        if (_ui == null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    /// <summary>Opens the start-up tabs (command-line folders, or the saved session) and the second list if it was open.</summary>
    public void Start()
    {
        Tree.Build();
        static bool Openable(string path) => Locations.IsVirtual(path) || Directory.Exists(path);
        var startPaths = _startPaths.Where(Openable).ToList();
        var pinned = new HashSet<int>(Settings.PinnedTabs ?? new());
        var paths = startPaths.Count > 0
            ? startPaths.Select(p => (Path: p, Pinned: false)).ToList()
            : Settings.Tabs.Select((p, i) => (Path: p, Pinned: pinned.Contains(i))).Where(t => Openable(t.Path)).ToList();
        if (paths.Count == 0)
            paths.Add((Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false));
        foreach (var (path, isPinned) in paths)
            Tabs.Add(new BrowserTab(Locations.Canonical(path)) { IsPinned = isPinned });
        int active = startPaths.Count > 0 ? 0 : Math.Clamp(Settings.ActiveTab, 0, Tabs.Count - 1);
        ActivateTab(Tabs[active]);
        if (Settings.DualPaneVisible)
            SetDualPane(true, Settings.DualPanePath);
    }

    // ── Window chrome ──

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    /// <summary>The address bar; typing changes it, Enter goes there (<see cref="CommitAddress"/>).</summary>
    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    public IReadOnlyList<Crumb> Crumbs
    {
        get => _crumbs;
        private set => Set(ref _crumbs, value);
    }

    /// <summary>The left list's title strip in 두 창 보기.</summary>
    public string MainPaneTitle => Locations.DisplayName(_active?.FullPath ?? "");
    public string MainPaneToolTip => Locations.AddressText(_active?.FullPath ?? "");

    /// <summary>The left list has the keyboard while two lists show (its title strip gets the accent line).</summary>
    public bool IsMainPaneHighlighted => _isDualVisible && !_isDualActive;

    public bool CanGoBack => IsDualActive ? Dual.CanGoBack : _active?.Back.Count > 0;
    public bool CanGoForward => IsDualActive ? Dual.CanGoForward : _active?.Forward.Count > 0;
    public bool CanGoUp => IsDualActive ? Dual.CanGoUp : !InVirtual || CurrentSearch is { Kind: SearchKind.Here };

    void UpdateTitle()
    {
        var selected = InfoSelection;
        string path = selected.Count == 1 ? selected[0].FullPath : ActiveFolder;
        string extra = selected.Count > 1 ? $" ({selected.Count:N0}개 선택)" : "";
        Title = $"{Locations.AddressText(path)}{extra} - {AppName}";
    }

    void UpdateNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        CommandManager.InvalidateRequerySuggested();
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
        if (!Locations.IsThisPc(path))
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                crumbs.Insert(0, new Crumb(Locations.DisplayName(dir.FullName), dir.FullName, true));
            if (path.StartsWith(@"\\"))
            {
                crumbs[0] = crumbs[0] with { ShowSeparator = false };   // network paths aren't under 내 PC
                return crumbs;
            }
        }
        // Local paths start from 내 PC, as in Explorer: 내 PC › Windows (C:) › Users › …
        crumbs.Insert(0, new Crumb(Locations.ThisPc, Locations.ThisPc, false));
        return crumbs;
    }

    // ── Tabs ──

    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    public BrowserTab ActiveTab => _active;

    /// <summary>The search or tag list the active tab shows, if any.</summary>
    public SearchLocation? CurrentSearch => _active?.Search;

    /// <summary>The active tab shows a list rather than a real folder (내 PC, search or tag results).</summary>
    public bool InVirtual => _active?.IsVirtual == true;

    /// <summary>The active tab shows 내 PC (drives and user folders) rather than a real folder.</summary>
    public bool InThisPc => _active?.IsThisPc == true;

    /// <summary>The main list shows search or tag results (the 위치 column is shown for them).</summary>
    public bool IsResultList => CurrentSearch != null;

    public void ActivateTab(BrowserTab tab)
    {
        if (_active != null)
            _active.IsActive = false;
        _active = tab;
        tab.IsActive = true;
        OnPropertyChanged(nameof(ActiveTab));
        ShowCurrent();
    }

    public void NewTab(string path)
    {
        var tab = new BrowserTab(Locations.Canonical(path));
        Tabs.Insert(Tabs.IndexOf(_active) + 1, tab);
        ActivateTab(tab);
    }

    public void CloseTab(BrowserTab tab)
    {
        if (tab.IsPinned)
        {
            Status.Note("고정된 탭은 닫을 수 없습니다. 탭의 핀을 눌러 고정을 해제하세요.", FileList);
            return;
        }
        if (Tabs.Count <= 1)
            return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab == _active)
            ActivateTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
        RequestSave();
    }

    public void CycleTab(int step) => ActivateTab(Tabs[(Tabs.IndexOf(_active) + step + Tabs.Count) % Tabs.Count]);

    void TogglePin(BrowserTab tab)
    {
        tab.IsPinned = !tab.IsPinned;
        RequestSave();
    }

    // ── Navigation ──

    /// <summary>Goes somewhere in the main list (the active tab).</summary>
    public void Navigate(string path, string? selectName = null)
    {
        try
        {
            path = Locations.Canonical(path);
        }
        catch
        {
            return;
        }
        if (!Locations.IsVirtual(path) && !Directory.Exists(path))
        {
            _s.Dialogs.Warn($"'{path}' 폴더를 찾을 수 없습니다.");
            return;
        }
        if (string.Equals(path, _active.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            if (selectName != null)
                FileList.Select(new[] { selectName });
            return;
        }
        _active.Visit(path);
        ShowCurrent(selectName);
    }

    /// <summary>Goes somewhere from the shared controls (address bar, tree, breadcrumb, menus): in the active list.</summary>
    public void Go(string path, string? selectName = null)
    {
        // 내 PC and result lists only open in the main list.
        if (IsDualActive && !Locations.IsVirtual(path))
            Dual.Navigate(path, selectName);
        else
            Navigate(path, selectName);
    }

    public void GoBack()
    {
        if (IsDualActive)
        {
            Dual.GoBack();
            return;
        }
        if (_active.StepBack() is string previous)
            ShowCurrent(Locations.ChildNameOf(previous, _active.FullPath));
    }

    public void GoForward()
    {
        if (IsDualActive)
        {
            Dual.GoForward();
            return;
        }
        if (_active.StepForward() is string previous)
            ShowCurrent(Locations.ChildNameOf(previous, _active.FullPath));
    }

    public void GoUp()
    {
        if (IsDualActive)
        {
            Dual.GoUp();
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
            Navigate(Locations.ThisPc, Locations.DisplayName(_active.FullPath));   // above a drive is 내 PC, as in Explorer
    }

    /// <summary>Updates every view for the active tab's current location.</summary>
    void ShowCurrent(string? selectName = null)
    {
        string path = _active.FullPath;
        if (_isDualActive)
        {
            _isDualActive = false;
            OnDualActiveChanged();
        }
        UpdateTitle();
        AddressText = Locations.AddressText(path);
        Crumbs = BuildCrumbs(path);
        OnPropertyChanged(nameof(MainPaneTitle));
        OnPropertyChanged(nameof(MainPaneToolTip));
        OnPropertyChanged(nameof(IsResultList));
        OnPropertyChanged(nameof(InVirtual));
        UpdateNavigationState();
        Search.Show(CurrentSearch);
        UpdateListNoteMarks();
        UpdateFavoriteState();
        if (!InVirtual)
            _s.Recent.AddFolder(path);
        RequestSave();

        Tree.Sync(path);
        Watch(path);
        Status.ShowDrive(path);
        _ = LoadFilesAsync(path, selectName == null ? null : new[] { selectName });
    }

    /// <summary>Address bar, breadcrumb, back/forward/up, favorite star, tree and title for the active list.</summary>
    void UpdateChrome()
    {
        string location = ActiveFolder;
        AddressText = IsDualActive ? location : Locations.AddressText(location);
        Crumbs = BuildCrumbs(location);
        UpdateNavigationState();
        UpdateFavoriteState();
        if (IsDualActive)
            Tree.ClearAnchor();
        Tree.Sync(location);
        Status.ShowDrive(location);
        UpdateTitle();
    }

    async Task LoadFilesAsync(string path, ICollection<string>? selectNames = null)
    {
        long started = Perf.Now;
        bool loaded = await FileList.LoadAsync(path, selectNames);
        Status.Update(FileList);
        if (!loaded)
            return;
        UpdateInfoPane();
        RecomputeCompare();
        Perf.Log($"load {Path.GetFileName(path)}: {Perf.Now - started} ms");
    }

    /// <summary>Reloads the main list, keeping its selection; with <paramref name="reloadTree"/>, the tree around it too.</summary>
    public void Refresh(bool reloadTree = true)
    {
        if (reloadTree)
        {
            Tree.ReloadSelected();
            Tree.Sync(_active.FullPath);
        }
        var selected = FileList.Selected.Select(f => f.Name).ToList();
        Status.ShowDrive(_active.FullPath);
        _ = LoadFilesAsync(_active.FullPath, selected);
    }

    /// <summary>The toolbar's and menu's 새로 고침: the active list.</summary>
    public void RefreshActive()
    {
        if (IsDualActive)
            _ = Dual.ReloadAsync();
        else
            Refresh();
    }

    void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = _s.Watch.Watch(path, OnFolderChanged, _s.Notes.FollowRename);
    }

    void OnFolderChanged()
    {
        if (_renamingItem != null)
        {
            _reloadAfterRename = true;   // a reload would throw away the name being typed
            return;
        }
        Refresh(reloadTree: false);
    }

    /// <summary>Enter in the address bar: a path, a file (its folder, with it selected), 내 PC, or a name in 내 PC.</summary>
    public void CommitAddress()
    {
        string text = Environment.ExpandEnvironmentVariables(AddressText.Trim().Trim('"'));
        if (text.Length == 0)
            return;
        if (Locations.IsThisPc(text))
            Navigate(Locations.ThisPc);
        else if (InThisPc && !Path.IsPathRooted(text))
        {
            // A name typed while in 내 PC ("문서", "Windows (C:)") means that entry.
            if (FileList.AllItems.FirstOrDefault(i => i.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase)) is { } match)
                Navigate(match.FullPath);
        }
        else
        {
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
        }
        Focus(FocusTarget.FileList);
    }

    /// <summary>Esc in the address bar: back to where the tab is.</summary>
    public void ResetAddress()
    {
        AddressText = Locations.AddressText(_active.FullPath);
        Focus(FocusTarget.FileList);
    }

    // ── View options ──

    public bool ShowHidden
    {
        get => _s.FileSystem.ShowHidden;
        set
        {
            if (_s.FileSystem.ShowHidden == value)
                return;
            _s.FileSystem.ShowHidden = value;
            OnPropertyChanged();
            RequestSave();
            Tree.Build();
            Tree.Sync(_active.FullPath);
            Refresh(reloadTree: false);
        }
    }

    /// <summary>Memo/tag markers before icons, bold names, and dots on folders that hold noted files.</summary>
    public bool ShowNoteMarks
    {
        get => _showNoteMarks;
        set
        {
            if (!Set(ref _showNoteMarks, value))
                return;
            UpdateListNoteMarks();
            RequestSave();
        }
    }

    /// <summary>
    /// The file list's markers: the toolbar toggle, except that search and tag results always show them
    /// (those lists are looked up by memo and tag, so which rows have them is the point).
    /// </summary>
    public bool ListNoteMarks
    {
        get => _listNoteMarks;
        private set => Set(ref _listNoteMarks, value);
    }

    void UpdateListNoteMarks() => ListNoteMarks = _showNoteMarks || CurrentSearch != null;

    /// <summary>Switches every open window between the light and dark color sets.</summary>
    public bool IsDarkMode
    {
        get => _s.Theme.IsDark;
        set
        {
            if (_s.Theme.IsDark == value)
                return;
            _s.Theme.Apply(value);
            OnPropertyChanged();
            RequestSave();   // remembered right away, so the next start opens in the same mode
        }
    }

    // ── Info pane (메모·태그, 속성, 미리보기) ──

    public const int MemoTab = 0, PropertiesTab = 1, PreviewTab = 2;

    public bool IsInfoPaneVisible
    {
        get => _isInfoPaneVisible;
        set
        {
            if (_isInfoPaneVisible == value)
                return;
            if (!value)
            {
                Notes.ResolvePendingEdit();
                Notes.CommitTags();
            }
            _isInfoPaneVisible = value;
            OnPropertyChanged();
            if (value)
                UpdateInfoPane();
            RequestSave();
        }
    }

    public int InfoTabIndex
    {
        get => _infoTabIndex;
        set
        {
            if (Set(ref _infoTabIndex, value))
                RequestSave();
        }
    }

    public bool IsPropertiesTabVisible => _isInfoPaneVisible && _infoTabIndex == PropertiesTab;
    public bool IsPreviewTabVisible => _isInfoPaneVisible && _infoTabIndex == PreviewTab;

    public InfoHeader InfoHeader
    {
        get => _infoHeader;
        private set => Set(ref _infoHeader, value);
    }

    /// <summary>Alt+P (preview) / Alt+Shift+P (details): show the info pane on that tab, or hide it if already shown.</summary>
    public void ToggleInfoTab(int tab)
    {
        if (IsInfoPaneVisible && InfoTabIndex == tab)
            IsInfoPaneVisible = false;
        else
        {
            IsInfoPaneVisible = true;
            InfoTabIndex = tab;
        }
    }

    /// <summary>The folder (or 내 PC / result list) of the active list.</summary>
    public string ActiveFolder => IsDualActive ? Dual.Folder : _active?.FullPath ?? "";

    /// <summary>The selection the info pane, Ctrl+M and AI work on: the active list's.</summary>
    public IReadOnlyList<FileItem> InfoSelection => IsDualActive ? Dual.Selected : FileList.Selected;

    /// <summary>The folder described when nothing is selected (null for 내 PC and result lists).</summary>
    string? InfoFolder => IsDualActive ? Dual.Folder : InVirtual ? null : _active.FullPath;

    ImageSource? InfoIcon => IsDualActive ? ShellInfo.GetIcon(Dual.Folder, true) : _active.Icon;
    string InfoTitle => IsDualActive ? Locations.DisplayName(Dual.Folder) : _active.Title;

    /// <summary>Points the memo/tag editor at the selected item, or at the current folder when nothing is selected.</summary>
    void UpdateInfoPane()
    {
        if (!_isInfoPaneVisible || _active == null)
            return;
        DetailsRequested?.Invoke();
        var selected = InfoSelection;
        string? path = selected.Count switch
        {
            0 => InfoFolder,   // 내 PC or a result list can't carry a memo
            1 => selected[0].FullPath,
            _ => null,
        };
        // Same target (e.g. the list refreshed because a file changed): keep the editor and caret as they are.
        if (path != null && string.Equals(path, Notes.Path, StringComparison.OrdinalIgnoreCase))
            return;
        Notes.ShowFor(path);
        InfoHeader = selected.Count switch
        {
            0 when path == null => new(InfoIcon, InfoTitle, (CurrentSearch?.Description ?? "드라이브와 주요 폴더") + " · 항목을 선택하면 메모·태그를 남길 수 있습니다."),
            0 => new(InfoIcon, InfoTitle, "현재 폴더 (항목을 선택하지 않으면 이 폴더의 정보를 보여 줍니다)"),
            1 => new(selected[0].Icon, selected[0].Name, selected[0].Description),
            _ => new(null, $"{selected.Count:N0}개 항목 선택됨",
                $"파일 크기 합계 {SizeFormat.Bytes(selected.Sum(i => i.Size ?? 0))} · 메모와 태그는 한 항목씩 작성할 수 있습니다."),
        };
    }

    /// <summary>Fills the 속성 tab for the selected item (or current folder); skipped while the tab is hidden.</summary>
    public void UpdateProperties()
    {
        if (!IsPropertiesTabVisible || _active == null)
            return;
        var selected = InfoSelection;
        Properties.Show(selected.Count switch
        {
            0 when InfoFolder is string folder => new PropertiesTarget.Item(folder),
            0 => new PropertiesTarget.Drives(FileList.VisibleItems.ToList()),
            1 => new PropertiesTarget.Item(selected[0].FullPath),
            _ => new PropertiesTarget.Selection(selected),
        });
    }

    /// <summary>Ctrl+M: show the memo of the selected item ready for editing.</summary>
    public void EditNote()
    {
        IsInfoPaneVisible = true;
        InfoTabIndex = MemoTab;
        UpdateInfoPane();
        if (Notes.CanEdit)
        {
            Notes.BeginEdit();
            Focus(FocusTarget.NoteBox);
        }
    }

    // ── Selection ──

    /// <summary>While the user drags a selection rectangle, only the status bar follows; the rest catches up at the end.</summary>
    public bool DeferSelectionUpdates { get; set; }

    void OnMainSelectionChanged()
    {
        Status.Update(FileList);
        if (DeferSelectionUpdates)
            return;
        OnSelectionSettled();
    }

    /// <summary>The selection is final (also called once a selection rectangle is released).</summary>
    public void OnSelectionSettled()
    {
        UpdateTitle();
        UpdateInfoPane();
    }

    // ── Memos changed elsewhere ──

    /// <summary>Memos changed somewhere: folders on screen may have gained or lost their "holds noted files" dot.</summary>
    void OnNotesChanged()
    {
        if (_notesChangedQueued)
            return;
        _notesChangedQueued = true;
        Defer(() =>
        {
            _notesChangedQueued = false;
            FileList.RefreshFolderMarks();
            Dual.List.RefreshNotes();
            Tree.RefreshMarks();
            Tree.RebuildTags(_active.FullPath);
        });
    }

    /// <summary>Re-reads memos for the rows on screen without disturbing the memo editor.</summary>
    void RefreshNoteColumns()
    {
        FileList.RefreshNotes();
        Notes.Reload();
    }

    /// <summary>Re-reads memos/tags after they were changed outside the memo pane (e.g. by the AI window).</summary>
    void ReloadNotes()
    {
        Notes.ResolvePendingEdit();
        FileList.RefreshNotes();
        Notes.Reload();
        FileList.RefreshFilter();
    }
}
