using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>What tree nodes need to load their children and show their memo markers.</summary>
public sealed class FolderTreeContext(IFileSystem files, INotesStore notes)
{
    public IFileSystem Files => files;

    /// <summary>A loaded node's children were brought up to date (added, removed or reordered).</summary>
    public event Action<FolderNode>? ChildrenUpdated;

    internal void OnChildrenUpdated(FolderNode node) => ChildrenUpdated?.Invoke(node);

    /// <summary>
    /// Runs a change to a node's children made in the background (not by the user): if it takes away the selected
    /// node, the view selects its parent, and that must not navigate there. Set by the tree.
    /// </summary>
    public Action<Action> Quietly { get; set; } = change => change();

    public NoteMark MarkFor(string path) =>
        notes.Get(path) != null ? NoteMark.Memo : notes.FolderHasNotes(path) ? NoteMark.InsideFolder : NoteMark.None;
}

/// <summary>A node in the folder tree. Children are loaded lazily on first expand.</summary>
public sealed class FolderNode : ObservableObject
{
    // The "has subfolders" marker until the node is expanded; one per node, since the tree binds its IsSelected/IsExpanded.
    // Seen only while subfolders are read in the background (network and WSL folders, the computers on the network).
    static FolderNode Placeholder() => Note("불러오는 중...");

    readonly FolderTreeContext? _context;
    readonly bool _exactIcon;
    // For a branch whose entries are found rather than read from a folder (the computers on the network).
    Func<IReadOnlyList<string>>? _find;
    int _discoveries;
    bool _isExpanded, _isSelected, _loaded, _isRenaming;
    bool _isLast, _isOnPath, _pathLineTop, _pathLineBottom;
    bool _probePending;
    int _reloads;
    DriveInfo? _drive;
    string _name;
    ImageSource? _icon;

    FolderNode(FolderTreeContext? context, string name, string? path, bool exactIcon)
    {
        _context = context;
        _name = name;
        FullPath = path;
        _exactIcon = exactIcon;
        Children.CollectionChanged += OnChildrenChanged;
    }

    void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Appending is the common case (loading thousands of folders): only the old and new last items change.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == Children.Count - 1)
        {
            if (Children.Count > 1)
                Children[^2].IsLast = false;
            Children[^1].IsLast = true;
            Children[^1].Parent = this;
            return;
        }
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].IsLast = i == Children.Count - 1;
            Children[i].Parent = this;
        }
    }

    /// <summary>The node this one is listed under (null for the top-level groups).</summary>
    public FolderNode? Parent { get; private set; }

    public IEnumerable<FolderNode> SelfAndAncestors()
    {
        for (var node = this; node != null; node = node.Parent)
            yield return node;
    }

    /// <summary>This node and everything loaded below it.</summary>
    public IEnumerable<FolderNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    /// <summary>Changes only once, when a drive's volume label arrives (see <see cref="ProbeAsync"/>).</summary>
    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public override string ToString() => Name;   // what screen readers and UI Automation read for the tree item

    /// <summary>Tree marker: the folder has its own memo/tags, something inside it does, or neither.</summary>
    public NoteMark MarkKind => IsVirtual || IsTag || _context == null ? NoteMark.None : _context.MarkFor(FullPath!);

    /// <summary>Re-evaluates the marker after memos changed.</summary>
    public void RefreshMark() => OnPropertyChanged(nameof(MarkKind));

    /// <summary>Null for virtual group nodes such as "즐겨찾기" and "내 PC".</summary>
    public string? FullPath { get; }

    public bool IsVirtual => FullPath == null;

    /// <summary>A real folder (not a group, not a tag).</summary>
    public bool IsFolder => !IsVirtual && !IsTag;

    /// <summary>
    /// Drives, shared folders and the folders pinned at the top (the user's own 바탕 화면, 다운로드, 문서) can be
    /// opened and pasted into, but not cut, deleted or renamed from the tree.
    /// </summary>
    public bool IsMovable => IsFolder && !IsRoot && !Locations.IsRoot(FullPath!);

    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>The favorite (folder or group) this node shows, if it is under "즐겨찾기".</summary>
    public Favorite? Favorite { get; private set; }

    public bool IsFavoriteGroup => Favorite is { IsGroup: true };

    /// <summary>A tag in the tree's 태그 branch; its FullPath is the tag's result list, not a folder.</summary>
    public string? TagName { get; private set; }

    public bool IsTag => TagName != null;

    /// <summary>Set for the tree's top-level branches that aren't folders (즐겨찾기, 태그).</summary>
    public ImageSource? BranchIcon { get; init; }

    public ImageSource? Icon => BranchIcon ?? (IsTag ? ShellInfo.TagIcon
        : IsFavoriteGroup
        ? _icon ??= ShellInfo.GetIcon("", true)
        : IsVirtual ? (Name == Locations.ThisPc ? ShellInfo.ThisPcIcon : null)
        : Locations.IsServer(FullPath) ? ShellInfo.ComputerIcon
        : _probePending ? ShellInfo.GetIcon("", true)   // the plain folder icon: asking the shell about the path could block
        : _icon ??= ShellInfo.GetIcon(FullPath!, true, _exactIcon));

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value))
                return;
            if (IsFavoriteGroup)
                Favorite!.IsExpanded = value;   // remembered in favorites.json
            else if (value && _find != null)
                _ = DiscoverAsync();   // every time: computers come and go
            else if (value && _loaded)
                RefreshChildren();   // opened again: folders may have come and gone since
            else if (value)
                LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>The tree shows an edit box in place of the name (F2).</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    // ── Tree connector lines ──

    /// <summary>Top-level node ("즐겨찾기", "내 PC"): drawn without connector lines.</summary>
    public bool IsRoot { get; private set; }

    /// <summary>Last among its siblings: the parent's vertical line stops at this node.</summary>
    public bool IsLast
    {
        get => _isLast;
        private set => Set(ref _isLast, value);
    }

    /// <summary>This node is the current folder or one of its ancestors (its branch line is red).</summary>
    public bool IsOnPath
    {
        get => _isOnPath;
        set => Set(ref _isOnPath, value);
    }

    /// <summary>The red path passes down the parent's vertical line beside this node, at least to its middle.</summary>
    public bool PathLineTop
    {
        get => _pathLineTop;
        set => Set(ref _pathLineTop, value);
    }

    /// <summary>The red path continues past this node to a later sibling.</summary>
    public bool PathLineBottom
    {
        get => _pathLineBottom;
        set => Set(ref _pathLineBottom, value);
    }

    public static FolderNode Group(string name, IEnumerable<FolderNode> children, ImageSource? icon = null)
    {
        var node = new FolderNode(null, name, null, false) { _loaded = true, _isExpanded = true, IsRoot = true, BranchIcon = icon };
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    public static FolderNode Folder(FolderTreeContext context, string path, string? name = null, bool exactIcon = false)
    {
        var node = new FolderNode(context, name ?? Locations.DisplayName(path), path, exactIcon);
        if (MayHaveSubfolders(context, path))
            node.Children.Add(Placeholder());
        else
            node._loaded = true;
        return node;
    }

    // Asking a network or WSL folder is a round trip each (and may start WSL): such a folder gets an expander,
    // and finds out what it holds when it is opened.
    static bool MayHaveSubfolders(FolderTreeContext context, string path) =>
        Locations.IsUnc(path) || context.Files.HasSubfolders(path);

    /// <summary>A folder at the top of the tree (바탕 화면, 다운로드, 문서), probed in the background like the drives.</summary>
    public static FolderNode Pinned(FolderTreeContext context, string path, string name)
    {
        var node = Unprobed(context, path, name);
        node.IsRoot = true;
        return node;
    }

    /// <summary>
    /// A top-level branch whose entries are found each time it is opened, off the UI thread (the computers on the
    /// network: discovery takes seconds). <paramref name="find"/> gives their paths; each becomes a folder node.
    /// </summary>
    public static FolderNode Discovered(FolderTreeContext context, string name, ImageSource? icon, Func<IReadOnlyList<string>> find)
    {
        var node = new FolderNode(context, name, null, false) { IsRoot = true, BranchIcon = icon, _find = find };
        node.Children.Add(Placeholder());
        return node;
    }

    async Task DiscoverAsync()
    {
        var find = _find!;
        int discovery = ++_discoveries;
        // Its own thread: discovery holds it for seconds, which would starve the thread pool.
        var found = await Task.Factory.StartNew(find, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (discovery != _discoveries)
            return;   // opened again meanwhile: the newer search is the one that counts
        var existing = Children.Where(c => c.FullPath != null).ToDictionary(c => c.FullPath!, StringComparer.OrdinalIgnoreCase);
        var nodes = found.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => existing.TryGetValue(path, out var node) ? node : Folder(_context!, path, path.TrimStart('\\'))).ToList();
        // Nothing found still leaves an expander, so folding and opening it again looks again.
        SyncChildren(nodes.Count > 0 ? nodes : new List<FolderNode> { Children.FirstOrDefault(c => c.Name == NothingFound) ?? Note(NothingFound) });
    }

    const string NothingFound = "찾은 컴퓨터 없음";

    /// <summary>A line of text in place of children (not a folder: selecting it goes nowhere).</summary>
    static FolderNode Note(string text) => new(null, text, null, false) { _loaded = true };

    /// <summary>
    /// A folder node made without touching the disk: it shows an expander and the plain folder icon until
    /// <see cref="ProbeAsync"/> finds out the rest. For the nodes built at start-up, where any folder or drive may be
    /// slow to answer (#137).
    /// </summary>
    static FolderNode Unprobed(FolderTreeContext context, string path, string name, DriveInfo? drive = null)
    {
        var node = new FolderNode(context, name, path, exactIcon: true) { _probePending = true, _drive = drive };
        node.Children.Add(Placeholder());
        return node;
    }

    /// <summary>
    /// Finishes a node made by <see cref="Unprobed"/>: whether it has subfolders, its own shell icon and, for a drive,
    /// its volume label. All of it is read off the UI thread — a disconnected network drive, a cloud drive that is
    /// still starting or a sleeping disk can take many seconds to answer, and the window must not wait for that.
    /// </summary>
    public async Task ProbeAsync()
    {
        if (!_probePending || _context == null)
            return;
        long started = Perf.Now;
        string path = FullPath!;
        var files = _context.Files;
        var drive = _drive;
        int reloads = _reloads;
        // Its own thread: an unreachable share holds it for the network timeout, which would starve the thread pool.
        var (label, hasSubfolders) = await Task.Factory.StartNew(() => Probe(files, path, drive),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await ShellInfo.PrefetchAsync(new[] { (path, true, _exactIcon, "") });
        _probePending = false;
        // Kept on the node, so drawing it never asks the shell on the UI thread, even after the icon cache is cleared.
        _icon = ShellInfo.TryGetCachedIcon(path, true, _exactIcon, out var icon) && icon != null ? icon : ShellInfo.GetIcon("", true);
        if (label != null)
            Name = label;
        // A reload meanwhile has already read the folder afresh.
        if (!hasSubfolders && !_loaded && reloads == _reloads)
        {
            Children.Clear();
            _loaded = true;
        }
        OnPropertyChanged(nameof(Icon));
        Perf.Log($"tree probe {drive?.Name ?? Path.GetFileName(path)}: {Perf.Now - started} ms");
    }

    static (string? Label, bool HasSubfolders) Probe(IFileSystem files, string path, DriveInfo? drive)
    {
        if (drive == null)
            return (null, files.HasSubfolders(path));
        var (label, ready) = Locations.DriveState(drive);
        return (label, ready);
    }

    public static FolderNode FromFavorite(FolderTreeContext context, Favorite favorite)
    {
        if (!favorite.IsGroup)
        {
            var folder = Unprobed(context, favorite.FullPath!, favorite.Name);
            folder.Favorite = favorite;
            return folder;
        }
        var group = new FolderNode(context, favorite.Name, null, false)
        {
            _loaded = true,
            _isExpanded = favorite.IsExpanded,
            Favorite = favorite,
        };
        foreach (var child in favorite.Children)
            group.Children.Add(FromFavorite(context, child));
        return group;
    }

    /// <summary>"#예산 (12)": opens the list of everything carrying the tag.</summary>
    public static FolderNode Tag(string tag, int count) =>
        new(null, $"#{tag} ({count:N0})", SearchLocation.Tag(tag).Path, false) { _loaded = true, TagName = tag };

    /// <summary>A drive under 내 PC, labelled by its type until <see cref="ProbeAsync"/> reads the volume label.</summary>
    public static FolderNode Drive(FolderTreeContext context, DriveInfo drive) =>
        Unprobed(context, drive.RootDirectory.FullName, Locations.DriveLabelByType(drive), drive);

    public void Reload()
    {
        if (IsVirtual || _context == null)
            return;
        _loaded = false;
        _reloads++;
        if (_isExpanded)
            LoadChildren();
        else
        {
            Children.Clear();
            if (MayHaveSubfolders(_context, FullPath!))
                Children.Add(Placeholder());
            else
                _loaded = true;
        }
    }

    /// <summary>Network and WSL folders: a sleeping server or WSL starting up can take seconds to answer.</summary>
    bool ReadsInBackground => Locations.IsUnc(FullPath);

    /// <summary>
    /// Re-reads the subfolders and updates the children to match (see <see cref="UpdateChildren(IEnumerable{string})"/>).
    /// A folder that can't be read just now (a network drive dropping out) keeps its children.
    /// </summary>
    /// <returns>Whether anything changed; always false for a folder read in the background, which reports through
    /// <see cref="FolderTreeContext.ChildrenUpdated"/> once read.</returns>
    public bool RefreshChildren()
    {
        if (!IsFolder || _context == null)
            return false;
        if (ReadsInBackground)
        {
            _ = ReadChildrenInBackgroundAsync();
            return false;
        }
        return _context.Files.TryGetSubfolders(FullPath!) is { } subfolders && UpdateChildren(subfolders.Select(Locations.ItemName));
    }

    /// <summary>Until the answer comes the node keeps what it shows (the first time, "불러오는 중...").</summary>
    async Task ReadChildrenInBackgroundAsync()
    {
        var files = _context!.Files;
        string path = FullPath!;
        int reloads = _reloads;
        var subfolders = await Task.Factory.StartNew(() => files.TryGetSubfolders(path),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (reloads != _reloads)
            return;   // reloaded meanwhile: that read is the one that counts
        if (subfolders != null)
            UpdateChildren(subfolders.Select(Locations.ItemName));
        else if (Children is [{ FullPath: null }])
            SyncChildren(new());   // can't be read: no expander promising subfolders
    }

    /// <summary>
    /// Brings the children in line with <paramref name="subfolders"/> (the folder's subfolder names now), keeping the
    /// nodes that are still there — and what is expanded below them — rather than reloading. Children not loaded
    /// yet stay that way; only whether the node can be expanded is updated. Returns whether anything changed.
    /// </summary>
    public bool UpdateChildren(IEnumerable<string> subfolders)
    {
        if (!IsFolder || _context == null)
            return false;
        var names = subfolders.ToList();
        if (!_loaded)
        {
            // Unopened, it holds just the expander placeholder.
            if (names.Count > 0)
                return false;
            Children.Clear();
            _loaded = true;
            return true;
        }
        names.Sort(Native.StrCmpLogicalW);   // the order GetSubfolders gives
        // Matched by exact name, so a change of case alone (a → A) shows the new name.
        var existing = Children.Where(c => c.FullPath != null).ToDictionary(c => c.Name, StringComparer.Ordinal);
        return SyncChildren(names.Select(n => existing.TryGetValue(n, out var node) ? node : Folder(_context, Path.Join(FullPath, n), n)).ToList());
    }

    /// <summary>
    /// Makes the children exactly <paramref name="wanted"/> by removing, moving and inserting, never by clearing: a
    /// node that stays keeps its place in the view, and its selection, which the view would otherwise move elsewhere.
    /// </summary>
    bool SyncChildren(List<FolderNode> wanted)
    {
        if (wanted.SequenceEqual(Children))
            return false;
        var change = () =>
        {
            var keep = wanted.ToHashSet();
            for (int i = Children.Count - 1; i >= 0; i--)
                if (!keep.Contains(Children[i]))
                    Children.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < Children.Count && Children[i] == wanted[i])
                    continue;
                int at = Children.IndexOf(wanted[i]);
                if (at >= 0)
                    Children.Move(at, i);
                else
                    Children.Insert(i, wanted[i]);
            }
        };
        if (_context != null)
            _context.Quietly(change);
        else
            change();
        _context?.OnChildrenUpdated(this);
        return true;
    }

    void LoadChildren()
    {
        if (_loaded || FullPath == null || _context == null)
            return;
        _loaded = true;
        if (ReadsInBackground)
        {
            _ = ReadChildrenInBackgroundAsync();
            return;
        }
        Children.Clear();
        foreach (var dir in _context.Files.GetSubfolders(FullPath))
            Children.Add(Folder(_context, dir.FullName, Locations.ItemName(dir)));
    }
}
