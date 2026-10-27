using System.IO;

namespace FileStar.Tests.Support;

/// <summary>Records every message and answers questions from a script (default: yes / OK / null).</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<string> Messages { get; } = new();
    public Queue<bool> Answers { get; } = new();
    public Queue<string?> Inputs { get; } = new();
    public Queue<string?> Folders { get; } = new();

    public void Info(string message, string? title = null) => Messages.Add("info: " + message);
    public void Warn(string message, string? title = null) => Messages.Add("warn: " + message);

    public bool Confirm(string message, string title)
    {
        Messages.Add("confirm: " + message);
        return Answers.Count == 0 || Answers.Dequeue();
    }

    public bool ConfirmOkCancel(string message, string title) => Confirm(message, title);

    public string? Ask(string title, string prompt, string initial = "")
    {
        Messages.Add($"ask: {title}");
        return Inputs.Count > 0 ? Inputs.Dequeue() : null;
    }

    public string? PickFolder(string title, string? initial) => Folders.Count > 0 ? Folders.Dequeue() : null;
}

public sealed class FakeWindows : IWindowService
{
    public List<string> Opened { get; } = new();
    public IReadOnlyList<(string Path, string NewName)>? BatchPlan { get; set; }
    public List<string>? AiFiles { get; set; }

    public void ShowFavoritesManager(string currentPath, Favorite? select = null) => Opened.Add("favorites");
    public bool ShowAiSettings(AiSettings settings) { Opened.Add("ai settings"); return false; }
    public void ShowAiSummary(IReadOnlyList<string> files, Action onSaved) => Opened.Add("ai summary: " + string.Join("|", files));
    public void ShowNotesRepair(Action onChanged) => Opened.Add("notes repair");
    public IReadOnlyList<(string Path, string NewName)>? AskBatchRename(IReadOnlyList<FileItem> items) { Opened.Add("batch rename"); return BatchPlan; }
    public Task<List<string>?> ChooseAiFilesAsync(IReadOnlyList<FileItem> selected) =>
        Task.FromResult<List<string>?>(AiFiles ?? selected.Select(i => i.FullPath).ToList());
}

public sealed class FakeShell : IShellService
{
    public List<string> Calls { get; } = new();
    public void Open(string path) => Calls.Add("open " + path);
    public void OpenInExplorer(string path, bool select) => Calls.Add($"explorer {path} {select}");
    public void ShowProperties(string path) => Calls.Add("properties " + path);
    public void StartNewWindow(string folder) => Calls.Add("new window " + folder);
    public void CopyText(string text) => Calls.Add("copy " + text);
}

public sealed class FakeTheme : IThemeService
{
    public bool IsDark { get; private set; }
    public void Apply(bool dark) => IsDark = dark;
}

public sealed class FakeUpdates : IUpdateService
{
    public string RepoUrl => "https://example.invalid/FileStar";
    public bool IsInstalled => false;
    public string CurrentVersion => "1.0.0";
    public Task<string?> CheckAndDownloadAsync(Action<int>? progress = null) => Task.FromResult<string?>(null);
    public void ApplyAndRestart() { }
    public void ApplyOnExit() { }
}

/// <summary>Never fires: tests reload explicitly.</summary>
public sealed class FakeWatch : IFolderWatchService
{
    public IDisposable Watch(string folder, Action changed, Action<string, string>? renamed = null) => new Nothing();

    sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Copies, moves and deletes with plain System.IO (no Windows dialogs, no Recycle Bin), keeping memos attached
/// and recording undo steps the way the real implementation does.
/// </summary>
public sealed class DirectFileOperations(INotesStore notes, IUndoHistory undo) : IFileOperations
{
    public List<string> ClipboardPaths { get; } = new();
    public bool ClipboardCut { get; private set; }

    public bool ClipboardHasFiles => ClipboardPaths.Count > 0;

    public void SetClipboard(IReadOnlyList<string> paths, bool cut)
    {
        ClipboardPaths.Clear();
        ClipboardPaths.AddRange(paths);
        ClipboardCut = cut;
    }

    public Task<bool> PasteAsync(string folder) => TransferAsync(ClipboardPaths.ToList(), folder, ClipboardCut);

    public Task<bool> TransferAsync(IReadOnlyList<string> sources, string folder, bool move, bool record = true)
    {
        var map = new List<(string, string)>();
        foreach (var source in sources)
        {
            string target = Path.Combine(folder, Path.GetFileName(source.TrimEnd('\\')));
            if (move)
                DiskPaths.Move(source, target);
            else if (Directory.Exists(source))
                CopyFolder(source, target);
            else
                File.Copy(source, target);
            map.Add((source, target));
        }
        notes.CarryOver(map, move);
        if (record && map.Count > 0)
            undo.Push(move ? new MoveUndo(map) : new CreateUndo(map.Select(m => m.Item2).ToList(), "복사"));
        return Task.FromResult(true);
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanently, bool noConfirm = false, bool record = true)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
        return Task.FromResult(true);
    }

    public string Rename(string path, string newName)
    {
        FileNames.EnsurePlainName(newName);
        string newPath = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, newName);
        DiskPaths.Move(path, newPath);
        notes.Rename(path, newPath);
        undo.Push(new RenameUndo(path, newPath));
        return newPath;
    }

    public string CreateFolder(string parent, string name)
    {
        FileNames.EnsurePlainName(name);
        string target = Path.Combine(parent, name);
        if (DiskPaths.Exists(target))
            throw new IOException($"'{name}'이(가) 이미 있습니다.");
        Directory.CreateDirectory(target);
        undo.Push(new CreateUndo(new[] { target }, "새 폴더"));
        return target;
    }
}

/// <summary>Everything a MainViewModel needs, with real stores in a temp folder and fakes for whatever shows UI.</summary>
public sealed class TestServices : IDisposable
{
    public TestServices()
    {
        Notes = SqliteNotesStore.Open(Data.Combine("notes.db"));
        Undo = new UndoHistory();
        FileOps = new DirectFileOperations(Notes, Undo);
        // Empty, not the first-run defaults: those are the real user's Desktop, Documents and Downloads.
        Favorites = JsonFavoritesStore.Load(Data.File("favorites.json", "[]"));
        Settings = new JsonSettingsStore(Data.Combine("settings.json"));
        Services = new AppServices(Notes, Favorites, Settings, Recent, Undo, FileOps, FileSystem, new FakeWatch(), Dialogs, Windows, Shell,
            new FakeTheme(), new FakeUpdates(), new JsonAiSettingsStore(Data.Combine("ai.json")));
    }

    public TempDir Data { get; } = new();
    public TempDir Files { get; } = new();
    public SqliteNotesStore Notes { get; }
    public UndoHistory Undo { get; }
    public DirectFileOperations FileOps { get; }
    public JsonFavoritesStore Favorites { get; }
    public JsonSettingsStore Settings { get; }
    public RecentItems Recent { get; } = new();
    public LocalFileSystem FileSystem { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeWindows Windows { get; } = new();
    public FakeShell Shell { get; } = new();
    public AppServices Services { get; }

    public void Dispose()
    {
        Notes.Dispose();
        Data.Dispose();
        Files.Dispose();
    }
}
