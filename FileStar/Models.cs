using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;

namespace FileStar;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A node in the folder tree. Children are loaded lazily on first expand.</summary>
public sealed class FolderNode : ObservableObject
{
    static readonly FolderNode Placeholder = new("", null, false);

    readonly bool _exactIcon;
    bool _isExpanded, _isSelected, _loaded;
    bool _isLast, _isOnPath, _pathLineTop, _pathLineBottom;
    ImageSource? _icon;

    FolderNode(string name, string? path, bool exactIcon)
    {
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

    public string Name { get; }

    public override string ToString() => Name;   // what screen readers and UI Automation read for the tree item

    /// <summary>Tree marker: 1 the folder has its own memo/tags, 3 something inside it does, 0 none.</summary>
    public int MarkKind => IsVirtual || IsTag ? 0
        : NotesStore.Get(FullPath!) != null ? 1
        : NotesStore.FolderHasNotes(FullPath!) ? 3 : 0;

    /// <summary>Re-evaluates the marker after memos changed.</summary>
    public void RefreshMark() => OnPropertyChanged(nameof(MarkKind));

    /// <summary>Null for virtual group nodes such as "즐겨찾기" and "내 PC".</summary>
    public string? FullPath { get; }

    public bool IsVirtual => FullPath == null;

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
        : IsVirtual ? (Name == FileSystem.ThisPc ? ShellInfo.ThisPcIcon : null)
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

    bool _isRenaming;

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
        var node = new FolderNode(name, null, false) { _loaded = true, _isExpanded = true, IsRoot = true, BranchIcon = icon };
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    public static FolderNode Folder(string path, string? name = null, bool exactIcon = false)
    {
        var node = new FolderNode(name ?? FileSystem.DisplayName(path), path, exactIcon);
        if (FileSystem.HasSubfolders(path))
            node.Children.Add(Placeholder);
        else
            node._loaded = true;
        return node;
    }

    public static FolderNode FromFavorite(Favorite favorite)
    {
        if (!favorite.IsGroup)
        {
            var folder = Folder(favorite.FullPath!, favorite.Name, exactIcon: true);
            folder.Favorite = favorite;
            return folder;
        }
        var group = new FolderNode(favorite.Name, null, false)
        {
            _loaded = true,
            _isExpanded = favorite.IsExpanded,
            Favorite = favorite,
        };
        foreach (var child in favorite.Children)
            group.Children.Add(FromFavorite(child));
        return group;
    }

    /// <summary>"#예산 (12)": opens the list of everything carrying the tag.</summary>
    public static FolderNode Tag(string tag, int count) =>
        new($"#{tag} ({count:N0})", SearchLocation.Tag(tag).Path, false) { _loaded = true, TagName = tag };

    public static FolderNode Drive(DriveInfo drive)
    {
        var node = new FolderNode(FileSystem.DriveLabel(drive), drive.RootDirectory.FullName, true);
        bool ready;
        try { ready = drive.IsReady; }
        catch { ready = false; }
        if (ready)
            node.Children.Add(Placeholder);
        else
            node._loaded = true;
        return node;
    }

    public void Reload()
    {
        if (IsVirtual)
            return;
        _loaded = false;
        if (_isExpanded)
            LoadChildren();
        else
        {
            Children.Clear();
            if (FileSystem.HasSubfolders(FullPath!))
                Children.Add(Placeholder);
            else
                _loaded = true;
        }
    }

    void LoadChildren()
    {
        if (_loaded || FullPath == null)
            return;
        _loaded = true;
        Children.Clear();
        foreach (var dir in FileSystem.GetSubfolders(FullPath))
            Children.Add(Folder(dir.FullName, dir.Name));
    }
}

/// <summary>A row in the file list.</summary>
public sealed class FileItem : ObservableObject
{
    FileNote? _note;
    bool _isRenaming;

    /// <summary>The name cell is showing an edit box (F2).</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    /// <summary>The user's memo about this file, if any.</summary>
    public FileNote? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
                OnPropertyChanged(nameof(NoteSummary));
                OnPropertyChanged(nameof(NoteText));
                OnPropertyChanged(nameof(Tags));
                OnPropertyChanged(nameof(MarkKind));
            }
        }
    }

    bool _containsNotes;

    /// <summary>A folder with memos or tags somewhere inside it.</summary>
    public bool ContainsNotes
    {
        get => _containsNotes;
        set
        {
            if (Set(ref _containsNotes, value))
                OnPropertyChanged(nameof(MarkKind));
        }
    }

    int _compareState;

    /// <summary>폴더 비교 against the other list (<see cref="FolderCompare"/> constants; 0 = not comparing).</summary>
    public int CompareState
    {
        get => _compareState;
        set
        {
            if (Set(ref _compareState, value))
                OnPropertyChanged(nameof(CompareText));
        }
    }

    public string CompareText => _compareState switch
    {
        FolderCompare.OnlyHere => "이쪽에만",
        FolderCompare.Newer => "더 최신",
        FolderCompare.Older => "더 오래됨",
        FolderCompare.Different => "다름",
        FolderCompare.Same => "같음",
        _ => "",
    };

    /// <summary>Changed after its memo was written: the memo may be out of date (shown as a red memo marker).</summary>
    public bool NoteStale => NotesStore.IsStale(_note, Modified, IsDirectory);

    /// <summary>Marker before the icon: 1 memo, 2 tags only, 3 folder holding noted items, 4 memo needing an update, 0 none.</summary>
    public int MarkKind => NoteStale ? 4 : HasNote ? 1 : Tags.Count > 0 ? 2 : ContainsNotes ? 3 : 0;

    /// <summary>True when there is memo text (tags alone don't show the ✎ mark).</summary>
    public bool HasNote => !string.IsNullOrEmpty(_note?.Text);
    public string NoteSummary => _note?.Summary ?? "";
    public string? NoteText => string.IsNullOrEmpty(_note?.Text) ? null : _note.Text;
    public IReadOnlyList<string> Tags => _note?.TagList ?? Array.Empty<string>();

    ImageSource? _icon;
    bool _iconLoaded;
    string? _typeName;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }

    // ── Used by the 내 PC view: folders before drives, in a fixed order, with real icons and descriptive types ──
    public int SortGroup { get; init; }
    public int SortOrder { get; init; }
    public bool ExactIcon { get; init; }
    public string? TypeOverride { init => _typeName = value; }

    /// <summary>A copy shown under a friendly name in 내 PC (e.g. "문서" for C:\Users\me\Documents).</summary>
    public FileItem ForThisPc(string name, int group, int order, string typeName) => new()
    {
        Name = name,
        FullPath = FullPath,
        IsDirectory = IsDirectory,
        Modified = Modified,
        Created = Created,
        Accessed = Accessed,
        Attributes = Attributes,
        SortGroup = group,
        SortOrder = order,
        ExactIcon = true,
        TypeOverride = typeName,
    };
    public bool IsHidden { get; init; }
    public long? Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public DateTime Accessed { get; init; }
    public FileAttributes Attributes { get; init; }

    /// <summary>Explorer-style attribute letters: R(읽기 전용) H(숨김) S(시스템) A(보관) C(압축) E(암호화).</summary>
    public string AttributesText =>
        (Attributes.HasFlag(FileAttributes.ReadOnly) ? "R" : "") +
        (Attributes.HasFlag(FileAttributes.Hidden) ? "H" : "") +
        (Attributes.HasFlag(FileAttributes.System) ? "S" : "") +
        (Attributes.HasFlag(FileAttributes.Archive) ? "A" : "") +
        (Attributes.HasFlag(FileAttributes.Compressed) ? "C" : "") +
        (Attributes.HasFlag(FileAttributes.Encrypted) ? "E" : "");

    /// <summary>Extension with the dot, e.g. ".pptx".</summary>
    public string RawExtension { get; init; } = "";

    public string Extension { get; init; } = "";

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _icon = ShellInfo.GetIcon(FullPath, IsDirectory, ExactIcon);
                _iconLoaded = true;
            }
            return _icon;
        }
    }

    public string TypeName => _typeName ??= ShellInfo.GetTypeName(RawExtension, IsDirectory);

    /// <summary>The folder it is in (the 위치 column of search results).</summary>
    public string FolderPath => Path.GetDirectoryName(FullPath.TrimEnd('\\')) ?? "";
    public string SizeText => Size is long size ? FileSystem.FormatKB(size) : "";
    public string ModifiedText => FormatDate(Modified);
    public string CreatedText => FormatDate(Created);
    public string AccessedText => FormatDate(Accessed);

    static string FormatDate(DateTime time) => time == default ? "" : time.ToString("yyyy-MM-dd tt h:mm:ss");

    public override string ToString() => Name;   // what screen readers and UI Automation read for the row

    public static FileItem? From(FileSystemInfo info)
    {
        try
        {
            bool isDirectory = info is DirectoryInfo;
            string ext = isDirectory ? "" : Path.GetExtension(info.Name);
            return new FileItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDirectory,
                IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                Size = info is FileInfo file ? file.Length : null,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
                Accessed = info.LastAccessTime,
                Attributes = info.Attributes,
                RawExtension = ext,
                Extension = ext.TrimStart('.').ToLowerInvariant(),
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Folders first, then by the chosen column; names compare naturally like Explorer.</summary>
public sealed class FileItemComparer(string key, bool descending) : IComparer
{
    public int Compare(object? x, object? y)
    {
        var a = (FileItem)x!;
        var b = (FileItem)y!;
        if (a.SortGroup != b.SortGroup)
            return a.SortGroup.CompareTo(b.SortGroup);
        if (a.SortOrder != b.SortOrder && key == "Name")
            return descending ? b.SortOrder.CompareTo(a.SortOrder) : a.SortOrder.CompareTo(b.SortOrder);
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        int result = key switch
        {
            // Items with a memo first, then alphabetically by memo.
            "Note" => a.HasNote != b.HasNote
                ? (a.HasNote ? -1 : 1)
                : string.Compare(a.NoteSummary, b.NoteSummary, StringComparison.CurrentCultureIgnoreCase),
            "Tags" => a.Tags.Count != b.Tags.Count && (a.Tags.Count == 0 || b.Tags.Count == 0)
                ? (a.Tags.Count > 0 ? -1 : 1)
                : string.Compare(string.Join(" ", a.Tags), string.Join(" ", b.Tags), StringComparison.CurrentCultureIgnoreCase),
            "Ext" => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            "Size" => Nullable.Compare(a.Size, b.Size),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            "Folder" => Native.StrCmpLogicalW(a.FolderPath, b.FolderPath),
            "Compare" => a.CompareState.CompareTo(b.CompareState),
            "Modified" => a.Modified.CompareTo(b.Modified),
            "Created" => a.Created.CompareTo(b.Created),
            "Accessed" => a.Accessed.CompareTo(b.Accessed),
            "Attributes" => string.CompareOrdinal(a.AttributesText, b.AttributesText),
            _ => 0,
        };
        if (result == 0)
            result = Native.StrCmpLogicalW(a.Name, b.Name);
        return descending ? -result : result;
    }
}

public sealed class BrowserTab(string path) : ObservableObject
{
    string _path = path;
    bool _isActive;

    public string FullPath
    {
        get => _path;
        set
        {
            if (Set(ref _path, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    public string Title => FileSystem.DisplayName(_path);
    public bool IsThisPc => FileSystem.IsThisPc(_path);

    public override string ToString() => Title;   // what screen readers and UI Automation read for the tab
    public ImageSource? Icon => IsThisPc ? ShellInfo.ThisPcIcon
        : SearchLocation.TryParse(_path) is { } search ? (search.Kind == SearchKind.Tag ? ShellInfo.TagIcon : ShellInfo.SearchIcon)
        : ShellInfo.GetIcon(_path, true, FileSystem.IsRoot(_path));

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    bool _isPinned;

    /// <summary>Pinned (고정) tabs can't be closed until they are unpinned.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();
}

public sealed record Crumb(string Name, string FullPath, bool ShowSeparator);

/// <summary>Turns ItemsControl.AlternationIndex into a 1-based row number.</summary>
public sealed class RowNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString() : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
