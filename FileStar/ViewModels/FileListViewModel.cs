using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;

namespace FileStar.ViewModels;

/// <summary>
/// The rows of one list: a folder, 내 PC, or a search / tag result list. Owns loading (off the UI thread), sorting,
/// filtering and the selection the view reports. The view selects rows when asked through <see cref="SelectionRequested"/>.
/// </summary>
public sealed class FileListViewModel : ObservableObject
{
    readonly IFileSystem _files;
    readonly INotesStore _notes;
    // Created on the UI thread (as the list view needs anyway); without a context, rows added one by one would
    // each renumber the whole list.
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    CancellationTokenSource? _loadCts;
    ListCollectionView? _view;
    IReadOnlyList<FileItem> _selected = Array.Empty<FileItem>();
    string _sortKey = "Name";
    bool _sortDescending;
    Predicate<FileItem>? _hide;
    string? _loadingMessage, _statusSuffix;
    string _emptyText = "이 폴더는 비어 있습니다.";
    bool _isEmpty, _renumberQueued;

    public FileListViewModel(IFileSystem files, INotesStore notes)
    {
        _files = files;
        _notes = notes;
    }

    /// <summary>The view should select these rows (and bring the first into view); an empty list clears the selection.</summary>
    public event Action<IReadOnlyList<FileItem>>? SelectionRequested;

    /// <summary>The view should scroll back to the top (a new folder was loaded without anything to select).</summary>
    public event Action? ScrollToTopRequested;

    /// <summary>The selection the view reported changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>What the list shows (sorted and filtered); null before the first load or when the folder can't be read.</summary>
    public ICollectionView? View
    {
        get => _view;
        private set
        {
            var view = (ListCollectionView?)value;
            if (view == _view)
                return;
            if (_view != null)
                ((INotifyCollectionChanged)_view).CollectionChanged -= OnOrderChanged;
            if (view != null)
            {
                ((INotifyCollectionChanged)view).CollectionChanged += OnOrderChanged;
                Number(view);   // before the list draws it
            }
            Set(ref _view, view);
        }
    }

    /// <summary>Every row, ignoring the filter.</summary>
    public IEnumerable<FileItem> AllItems => _view?.SourceCollection.Cast<FileItem>() ?? Enumerable.Empty<FileItem>();

    /// <summary>The rows in display order (sorted, filtered).</summary>
    public IEnumerable<FileItem> VisibleItems => _view?.Cast<FileItem>() ?? Enumerable.Empty<FileItem>();

    public IReadOnlyList<FileItem> Selected => _selected;

    /// <summary>"불러오는 중...", "검색 중... 12개 찾음" or why the folder can't be shown; null once the rows are there.</summary>
    public string? LoadingMessage
    {
        get => _loadingMessage;
        private set => Set(ref _loadingMessage, value);
    }

    /// <summary>Added to the status bar after a search: what was searched, or that results were capped.</summary>
    public string? StatusSuffix => _statusSuffix;

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public string SortKey => _sortKey;
    public bool SortDescending => _sortDescending;

    public void SetSort(string key, bool descending)
    {
        _sortKey = key;
        _sortDescending = descending;
        if (_view != null)
            _view.CustomSort = new FileItemComparer(key, descending);
    }

    /// <summary>Rows for which <paramref name="hide"/> is true are left out (폴더 비교 "다른 것만 보기").</summary>
    public void SetFilter(Predicate<FileItem>? hide)
    {
        _hide = hide;
        RefreshFilter();
    }

    public void RefreshFilter()
    {
        _view?.Refresh();   // the view already holds Filter (ShowItems); setting it again would refresh twice
    }

    bool Filter(object item) => _hide == null || !_hide((FileItem)item);

    public void SetSelection(IReadOnlyList<FileItem> selected)
    {
        _selected = selected;
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects the rows with these names (as currently loaded).</summary>
    public void Select(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        SelectionRequested?.Invoke(VisibleItems.Where(i => set.Contains(i.Name)).ToList());
    }

    public void Select(FileItem item) => SelectionRequested?.Invoke(new[] { item });

    public FileItem? Find(string path) =>
        AllItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));

    public FileItem? FindByName(string name) =>
        VisibleItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row <paramref name="offset"/> places away in the list as currently sorted and filtered.</summary>
    public FileItem? NeighborOf(FileItem item, int offset)
    {
        if (_view == null)
            return null;
        int index = _view.IndexOf(item) + offset;
        return index >= 0 && index < _view.Count ? _view.GetItemAt(index) as FileItem : null;
    }

    /// <summary>Re-reads memos for every row (they changed outside the list).</summary>
    public void RefreshNotes()
    {
        foreach (var row in AllItems)
            row.ApplyNotes(_notes);
    }

    /// <summary>Only the "holds noted files" dots on folders (cheap enough to run after every memo change).</summary>
    public void RefreshFolderMarks()
    {
        foreach (var row in AllItems.Where(r => r.IsDirectory))
            row.ContainsNotes = _notes.FolderHasNotes(row.FullPath);
    }

    /// <summary>The memo of <paramref name="path"/> was saved: rows showing it show the new note.</summary>
    public void UpdateNote(string path, FileNote? note)
    {
        foreach (var row in AllItems.Where(r => string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            row.Note = note;
    }

    /// <summary>
    /// Loads <paramref name="location"/> (a folder, 내 PC, or a search) and selects <paramref name="selectNames"/>.
    /// A newer load cancels this one. Returns false if it was superseded or failed.
    /// </summary>
    /// <param name="scrollToTop">With nothing to select, start at the top (false: stay where the user scrolled).</param>
    public async Task<bool> LoadAsync(string location, ICollection<string>? selectNames = null, bool scrollToTop = true)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _statusSuffix = null;
        LoadingMessage = "불러오는 중...";
        EmptyText = "이 폴더는 비어 있습니다.";
        bool loaded = SearchLocation.TryParse(location) is { } search
            ? await LoadSearchAsync(search, cts.Token)
            : await LoadFolderAsync(location, cts.Token);
        if (!loaded || cts.IsCancellationRequested)
            return false;
        if (selectNames is { Count: > 0 })
            Select(selectNames);
        else if (scrollToTop && SearchLocation.TryParse(location) == null)
            ScrollToTopRequested?.Invoke();
        return true;
    }

    async Task<bool> LoadFolderAsync(string path, CancellationToken token)
    {
        List<FileItem> items;
        try
        {
            items = await Task.Run(() => Locations.IsThisPc(path)
                ? ThisPcItems.Build()
                : _files.GetEntries(path).Select(FileItem.From).OfType<FileItem>().ToList(), token);
            // Icons and type names off the UI thread, so drawing the list doesn't stall on the shell.
            await ShellInfo.PrefetchAsync(items.Select(i => i.ShellKey).ToList());
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
                return false;
            View = null;
            IsEmpty = false;
            LoadingMessage = "폴더를 열 수 없습니다: " + ex.Message;
            return false;
        }
        if (token.IsCancellationRequested)
            return false;
        foreach (var item in items)
            item.ApplyNotes(_notes);
        ShowItems(items);
        IsEmpty = items.Count == 0;
        LoadingMessage = null;
        return true;
    }

    /// <summary>
    /// Fills the list with a search's results as they are found: memos and tags first (instant), then names
    /// from walking the folder tree. Leaving the location cancels the walk.
    /// </summary>
    async Task<bool> LoadSearchAsync(SearchLocation search, CancellationToken token)
    {
        var items = new ObservableCollection<FileItem>();
        ShowItems(items);
        IsEmpty = false;
        LoadingMessage = "검색 중...";
        var notes = _notes.Snapshot();
        var skip = _files.AttributesToSkip;
        // Progress<T> hands each batch back on this (UI) thread.
        var found = new Progress<List<FileItem>>(batch =>
        {
            if (token.IsCancellationRequested)
                return;
            foreach (var item in batch)
            {
                item.ApplyNotes(_notes);
                items.Add(item);
            }
            LoadingMessage = $"검색 중... {items.Count:N0}개 찾음";
        });
        IProgress<List<FileItem>> report = found;
        bool full;
        try
        {
            full = await Task.Run(() => SearchRunner.Run(search, notes, skip,
                batch => report.Report(batch.Select(FileItem.FromPath).OfType<FileItem>().ToList()), token), token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                LoadingMessage = "검색할 수 없습니다: " + ex.Message;
            return false;
        }
        // Let the last batch reach the list before counting.
        await Task.Yield();
        if (token.IsCancellationRequested)
            return false;
        EmptyText = search.Query.Length == 0 && search.Kind != SearchKind.Tag ? "검색어를 입력하고 Enter를 누르세요." : "찾은 항목이 없습니다.";
        IsEmpty = items.Count == 0;
        _statusSuffix = full ? $" · 결과가 많아 {SearchRunner.MaxResults:N0}개까지만 표시 (검색어를 좁혀 보세요)" : $" · {search.Description}";
        LoadingMessage = null;
        return true;
    }

    // Sorting, filtering and reloading renumber at once. Rows a search adds one by one are numbered once per
    // batch: renumbering on every insert would be quadratic over a few thousand results.
    void OnOrderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || _ui == null)
            Renumber();
        else if (!_renumberQueued)
        {
            _renumberQueued = true;
            _ui.Post(_ => Renumber(), null);
        }
    }

    void Renumber()
    {
        _renumberQueued = false;
        if (_view != null)
            Number(_view);
    }

    static void Number(ListCollectionView view)
    {
        int number = 1;
        foreach (FileItem item in view)
            item.RowNumber = number++;
    }

    void ShowItems(System.Collections.IList items) =>
        View = new ListCollectionView(items) { CustomSort = new FileItemComparer(_sortKey, _sortDescending), Filter = Filter };
}
