using System.IO;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>
/// 두 창 보기: the right-hand folder list. It has its own folder and history; the window's address bar,
/// back/forward/up, tree and info pane serve it while it is the active list. File operations go through
/// <see cref="IFileOperations"/>, so memos follow and Ctrl+Z works.
/// </summary>
public sealed class FolderPaneViewModel : ObservableObject, IDisposable
{
    readonly IFileOperations _fileOps;
    readonly IDialogService _dialogs;
    readonly IShellService _shell;
    readonly IFolderWatchService _watch;
    readonly Stack<string> _back = new(), _forward = new();
    IDisposable? _watcher;
    string _folder = "";
    bool _isActive;

    public FolderPaneViewModel(IFileSystem files, INotesStore notes, IFileOperations fileOps, IDialogService dialogs,
        IShellService shell, IFolderWatchService watch)
    {
        _fileOps = fileOps;
        _dialogs = dialogs;
        _shell = shell;
        _watch = watch;
        List = new FileListViewModel(files, notes);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public FileListViewModel List { get; }

    /// <summary>The folder changed (the window updates its address bar, tree and info pane).</summary>
    public event Action? Navigated;

    /// <summary>The list was (re)loaded: compare results need recomputing.</summary>
    public event Action? Reloaded;

    /// <summary>Files changed through the pane: the main list may show them too.</summary>
    public event Action? FilesChanged;

    public event Action? CloseRequested;

    public ICommand CloseCommand { get; }

    /// <summary>The folder the main list shows (null for 내 PC and result lists), for 왼쪽 창으로 복사/이동.</summary>
    public Func<string?> MainFolder { get; set; } = () => null;

    /// <summary>The folder shown (empty until <see cref="Navigate"/>).</summary>
    public string Folder
    {
        get => _folder;
        private set
        {
            if (Set(ref _folder, value))
                OnPropertyChanged(nameof(Title));
        }
    }

    public string Title => _folder.Length == 0 ? "" : Locations.DisplayName(_folder);

    public bool HasFolder => _folder.Length > 0;

    /// <summary>The accent line under the title strip marks the list that has the keyboard.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => _folder.Length > 0 && Directory.GetParent(_folder) != null;

    public IReadOnlyList<FileItem> Selected => List.Selected;

    public void Navigate(string path, string? selectName = null)
    {
        try
        {
            path = Locations.Normalize(path);
        }
        catch
        {
            return;
        }
        if (!Directory.Exists(path))
        {
            _dialogs.Warn($"'{path}' 폴더를 찾을 수 없습니다.");
            return;
        }
        if (string.Equals(_folder, path, StringComparison.OrdinalIgnoreCase))
        {
            if (selectName != null)
                List.Select(new[] { selectName });
            return;
        }
        if (_folder.Length > 0)
            _back.Push(_folder);
        _forward.Clear();
        _ = ShowAsync(path, selectName);
    }

    public void GoBack()
    {
        if (_back.Count == 0)
            return;
        string previous = _folder;
        _forward.Push(previous);
        _ = ShowAsync(_back.Pop(), Path.GetFileName(previous));
    }

    public void GoForward()
    {
        if (_forward.Count == 0)
            return;
        _back.Push(_folder);
        _ = ShowAsync(_forward.Pop());
    }

    public void GoUp()
    {
        if (_folder.Length > 0 && Directory.GetParent(_folder) is { } parent)
            Navigate(parent.FullName, Path.GetFileName(_folder));
    }

    async Task ShowAsync(string path, string? selectName = null)
    {
        Folder = path;
        _watcher?.Dispose();
        _watcher = _watch.Watch(path, () => _ = ReloadAsync());
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        Navigated?.Invoke();
        await ReloadAsync(selectName == null ? null : new[] { selectName });
    }

    /// <summary>Reloads the folder, keeping the selection (or selecting <paramref name="select"/>).</summary>
    public async Task ReloadAsync(IEnumerable<string>? select = null)
    {
        if (_folder.Length == 0)
            return;
        var keep = (select ?? List.Selected.Select(i => i.Name)).ToList();
        // A reload after a change in the folder must not throw the user back to the top.
        if (await List.LoadAsync(_folder, keep, scrollToTop: false))
            Reloaded?.Invoke();
    }

    public void OpenSelected()
    {
        var items = List.Selected;
        if (items is [{ IsDirectory: true } folder])
            Navigate(folder.FullPath);
        else
            foreach (var item in items.Where(i => !i.IsDirectory))
                OpenFile(item.FullPath);
    }

    void OpenFile(string path)
    {
        try
        {
            _shell.Open(path);
        }
        catch (Exception ex)
        {
            _dialogs.Warn(ex.Message, "열 수 없음");
        }
    }

    // ── File operations ──

    List<string> SelectedPaths => List.Selected.Select(i => i.FullPath).ToList();

    public void SetClipboard(bool cut)
    {
        var paths = SelectedPaths;
        if (paths.Count > 0)
            _fileOps.SetClipboard(paths, cut);
    }

    public async Task PasteAsync()
    {
        if (_folder.Length > 0 && await _fileOps.PasteAsync(_folder))
            await AfterChangeAsync();
    }

    public async Task DeleteAsync(bool permanently)
    {
        var paths = SelectedPaths;
        if (paths.Count > 0 && await _fileOps.DeleteAsync(paths, permanently))
            await AfterChangeAsync();
    }

    public async Task RenameAsync()
    {
        if (List.Selected is not [var item])
            return;
        if (_dialogs.Ask("이름 바꾸기", "새 이름:", item.Name) is not string newName || newName == item.Name)
            return;
        try
        {
            _fileOps.Rename(item.FullPath, newName);
        }
        catch (Exception ex)
        {
            _dialogs.Warn("이름을 바꿀 수 없습니다: " + ex.Message);
            return;
        }
        await ReloadAsync(new[] { newName });
        FilesChanged?.Invoke();
    }

    /// <summary>Asks for a name (suggesting "새 폴더") and creates the folder here.</summary>
    public async Task NewFolderAsync()
    {
        if (_folder.Length == 0)
            return;
        if (_dialogs.Ask("새 폴더", "폴더 이름:", Locations.NewFolderName(_folder)) is not string name)
            return;
        try
        {
            _fileOps.CreateFolder(_folder, name);
        }
        catch (Exception ex)
        {
            _dialogs.Warn("새 폴더를 만들 수 없습니다: " + ex.Message);
            return;
        }
        await ReloadAsync(new[] { name });
        FilesChanged?.Invoke();
    }

    /// <summary>Copies or moves the selection into the main list's folder.</summary>
    public async Task SendToMainAsync(bool move)
    {
        var paths = SelectedPaths;
        if (paths.Count > 0 && MainFolder() is string target && await _fileOps.TransferAsync(paths, target, move))
            await AfterChangeAsync();
    }

    /// <summary>Copies or moves files from elsewhere (the main list, a drop) into <paramref name="folder"/> (this pane's by default).</summary>
    public async Task ReceiveAsync(IReadOnlyList<string> paths, bool move, string? folder = null)
    {
        folder ??= _folder;
        if (folder.Length > 0 && paths.Count > 0 && await _fileOps.TransferAsync(paths, folder, move))
            await AfterChangeAsync();
    }

    async Task AfterChangeAsync()
    {
        await ReloadAsync();
        FilesChanged?.Invoke();
    }

    /// <summary>The pane's right-click menu.</summary>
    public IReadOnlyList<MenuItemViewModel> ContextMenu()
    {
        var items = List.Selected;
        bool any = items.Count > 0;
        string? main = MainFolder();
        string mainName = main == null ? "" : $" ({Locations.DisplayName(main)})";
        return new[]
        {
            MenuItemViewModel.Action("열기", OpenSelected, any, "Enter"),
            MenuItemViewModel.Separator,
            MenuItemViewModel.Action("왼쪽 창으로 복사" + mainName, () => _ = SendToMainAsync(move: false), any && main != null),
            MenuItemViewModel.Action("왼쪽 창으로 이동" + mainName, () => _ = SendToMainAsync(move: true), any && main != null),
            MenuItemViewModel.Separator,
            MenuItemViewModel.Action("잘라내기", () => SetClipboard(cut: true), any, "Ctrl+X"),
            MenuItemViewModel.Action("복사", () => SetClipboard(cut: false), any, "Ctrl+C"),
            MenuItemViewModel.Action("붙여넣기", () => _ = PasteAsync(), _fileOps.ClipboardHasFiles, "Ctrl+V"),
            MenuItemViewModel.Action("삭제", () => _ = DeleteAsync(permanently: false), any, "Del"),
            MenuItemViewModel.Action("이름 바꾸기", () => _ = RenameAsync(), items.Count == 1, "F2"),
            MenuItemViewModel.Separator,
            MenuItemViewModel.Action("새 폴더", () => _ = NewFolderAsync(), _folder.Length > 0, "Ctrl+Shift+N"),
            MenuItemViewModel.Action("새로 고침", () => _ = ReloadAsync(), true, "F5"),
        };
    }

    public void Dispose() => _watcher?.Dispose();
}
