using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>What tree nodes need to load their children and show their memo markers.</summary>
public sealed class FolderTreeContext(IFileSystem files, INotesStore notes)
{
    public IFileSystem Files => files;

    public NoteMark MarkFor(string path) =>
        notes.Get(path) != null ? NoteMark.Memo : notes.FolderHasNotes(path) ? NoteMark.InsideFolder : NoteMark.None;
}

/// <summary>A node in the folder tree. Children are loaded lazily on first expand.</summary>
public sealed class FolderNode : ObservableObject
{
    // The "has subfolders" marker until the node is expanded; one per node, since the tree binds its IsSelected/IsExpanded.
    static FolderNode Placeholder() => new(null, "", null, false);

    readonly FolderTreeContext? _context;
    readonly bool _exactIcon;
    bool _isExpanded, _isSelected, _loaded, _isRenaming;
    bool _isLast, _isOnPath, _pathLineTop, _pathLineBottom;
    ImageSource? _icon;

    FolderNode(FolderTreeContext? context, string name, string? path, bool exactIcon)
    {
        _context = context;
        Name = name;
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

    public string Name { get; }

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

    /// <summary>Drives can be opened and pasted into, but not cut, deleted or renamed.</summary>
    public bool IsMovable => IsFolder && !Locations.IsRoot(FullPath!);

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
        if (context.Files.HasSubfolders(path))
            node.Children.Add(Placeholder());
        else
            node._loaded = true;
        return node;
    }

    public static FolderNode FromFavorite(FolderTreeContext context, Favorite favorite)
    {
        if (!favorite.IsGroup)
        {
            var folder = Folder(context, favorite.FullPath!, favorite.Name, exactIcon: true);
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

    public static FolderNode Drive(FolderTreeContext context, DriveInfo drive)
    {
        var node = new FolderNode(context, Locations.DriveLabel(drive), drive.RootDirectory.FullName, true);
        bool ready;
        try { ready = drive.IsReady; }
        catch { ready = false; }
        if (ready)
            node.Children.Add(Placeholder());
        else
            node._loaded = true;
        return node;
    }

    public void Reload()
    {
        if (IsVirtual || _context == null)
            return;
        _loaded = false;
        if (_isExpanded)
            LoadChildren();
        else
        {
            Children.Clear();
            if (_context.Files.HasSubfolders(FullPath!))
                Children.Add(Placeholder());
            else
                _loaded = true;
        }
    }

    void LoadChildren()
    {
        if (_loaded || FullPath == null || _context == null)
            return;
        _loaded = true;
        Children.Clear();
        foreach (var dir in _context.Files.GetSubfolders(FullPath))
            Children.Add(Folder(_context, dir.FullName, dir.Name));
    }
}
