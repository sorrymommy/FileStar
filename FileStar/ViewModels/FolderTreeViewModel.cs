using System.Collections.ObjectModel;
using System.IO;

namespace FileStar.ViewModels;

/// <summary>
/// The folder tree: 즐겨찾기, the pinned 바탕 화면 · 다운로드 · 문서, 내 PC (drives), 네트워크, Linux (WSL) and 태그.
/// Follows the location being shown — expanding down to it, selecting it and drawing the red path line — and stays
/// inside the favorite or pinned folder's branch while browsing below it. A location it doesn't list (inside a hidden
/// folder, say) gets the path line as far as it goes, with nothing selected.
/// </summary>
public sealed class FolderTreeViewModel : ObservableObject
{
    readonly FolderTreeContext _context;
    readonly IFavoritesStore _favorites;
    readonly INotesStore _notes;
    readonly ITreePlaces _places;
    FolderNode? _favoritesNode, _pcNode, _networkNode, _linuxNode, _tagsNode, _selected;
    List<FolderNode> _pinnedNodes = new();
    List<FolderNode> _pathChain = new();
    string? _location;
    int _quiet;

    public FolderTreeViewModel(FolderTreeContext context, IFavoritesStore favorites, INotesStore notes, ITreePlaces places)
    {
        _context = context;
        _favorites = favorites;
        _notes = notes;
        _places = places;
        _context.ChildrenUpdated += OnChildrenUpdated;
        _context.Quietly = Quietly;
    }

    /// <summary>The user picked a node: show this location.</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>The view should realize and scroll to the last node of this chain (the tree is virtualized).</summary>
    public event Action<IReadOnlyList<FolderNode>>? BringIntoViewRequested;

    public ObservableCollection<FolderNode> Roots { get; } = new();

    public FolderNode? Selected => _selected;

    public FolderNode? FavoritesNode => _favoritesNode;

    /// <summary>The nodes from the root down to the location shown, as far as the tree lists it.</summary>
    public IReadOnlyList<FolderNode> PathChain => _pathChain;

    /// <summary>
    /// The favorite or pinned folder the current location was reached through (the tree stays in its branch), or null
    /// (the tree follows the location under 내 PC).
    /// </summary>
    public FolderNode? Anchor { get; private set; }

    /// <summary>The selected node, if it is a real folder (what keyboard commands on the tree act on).</summary>
    public FolderNode? SelectedFolder => _selected is { IsFolder: true } node ? node : null;

    public void Build()
    {
        Roots.Clear();
        _selected = null;
        _pathChain = new();
        _favoritesNode = FolderNode.Group("즐겨찾기", FavoriteNodes(), ShellInfo.FavoritesIcon);
        Roots.Add(_favoritesNode);
        _pinnedNodes = _places.PinnedFolders().Select(f => FolderNode.Pinned(_context, f.Path, f.Name)).ToList();
        foreach (var pinned in _pinnedNodes)
            Roots.Add(pinned);
        _pcNode = FolderNode.Group(Locations.ThisPc, DriveInfo.GetDrives().Select(d => FolderNode.Drive(_context, d)));
        Roots.Add(_pcNode);
        // Folded until opened: opening 네트워크 looks for computers, opening a distribution starts WSL.
        _networkNode = _places.IsNetworkAvailable ? FolderNode.Discovered(_context, "네트워크", ShellInfo.NetworkIcon, _places.FindComputers) : null;
        AddFolded(_networkNode);
        var distros = _places.LinuxDistros();
        _linuxNode = distros.Count > 0
            ? FolderNode.Group("Linux", distros.Select(d => FolderNode.Folder(_context, d, Locations.LastSegment(d))), ShellInfo.LinuxIcon)
            : null;
        AddFolded(_linuxNode);
        // Tags start folded: there can be many.
        _tagsNode = FolderNode.Group("태그", TagNodes(), ShellInfo.TagIcon);
        AddFolded(_tagsNode);
        if (Anchor != null)
            Anchor = FindAnchor(Anchor.FullPath!);
        Probing = Task.WhenAll(ProbeFavoritesAsync(), ProbeAsync(_pcNode), Task.WhenAll(_pinnedNodes.Select(n => n.ProbeAsync())));
    }

    void AddFolded(FolderNode? node)
    {
        if (node == null)
            return;
        node.IsExpanded = false;
        Roots.Add(node);
    }

    /// <summary>
    /// Completes once the favorites and drives the tree last built have their labels, icons and expanders. They are
    /// built without touching the disk and probed in the background, so a slow drive never holds up the window.
    /// </summary>
    public Task Probing { get; private set; } = Task.CompletedTask;

    /// <summary>The favorite folders' own icons are now cached (e.g. for the 즐겨찾기 menu).</summary>
    public event Action? FavoritesProbed;

    async Task ProbeFavoritesAsync()
    {
        await ProbeAsync(_favoritesNode!);
        FavoritesProbed?.Invoke();
    }

    /// <summary>Each node on its own, so one slow drive doesn't hold up the others.</summary>
    static Task ProbeAsync(FolderNode root) => Task.WhenAll(root.SelfAndDescendants().ToList().Select(n => n.ProbeAsync()));

    IEnumerable<FolderNode> FavoriteNodes() => _favorites.Items.Select(f => FolderNode.FromFavorite(_context, f));

    IEnumerable<FolderNode> TagNodes() => _notes.TagCounts().Select(t => FolderNode.Tag(t.Tag, t.Count));

    /// <summary>Runs <paramref name="action"/> without tree selection changes navigating anywhere (e.g. a reload dropping the selected node).</summary>
    public void Quietly(Action action)
    {
        _quiet++;
        try
        {
            action();
        }
        finally
        {
            _quiet--;
        }
    }

    /// <summary>The view's selection changed (by the user, or because a node was selected here).</summary>
    public void OnSelected(FolderNode node)
    {
        _selected = node;
        if (_quiet > 0)
            return;
        // Remember whether this node is inside a favorite or pinned folder, so later moves stay in that branch.
        Anchor = node.SelfAndAncestors().FirstOrDefault(IsAnchor);
        if (!node.IsVirtual)
            NavigateRequested?.Invoke(node.FullPath!);
        else if (node == _pcNode)
            NavigateRequested?.Invoke(Locations.ThisPc);
    }

    /// <summary>Going to a favorite: keep the tree inside its 즐겨찾기 branch.</summary>
    public void AnchorToFavorite(string path) => Anchor = FindAnchor(path);

    public void ClearAnchor() => Anchor = null;

    bool IsAnchor(FolderNode node) => node.Favorite is { IsGroup: false } || _pinnedNodes.Contains(node);

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it, if the tree lists it.</summary>
    public void Sync(string path)
    {
        if (_pcNode == null)
            return;
        _location = path;
        if (Locations.IsThisPc(path))
        {
            Anchor = null;
            SelectPath(_pcNode, new List<FolderNode> { _pcNode });
            return;
        }
        if (SearchLocation.TryParse(path) is { } search)
        {
            Anchor = null;
            // A tag list selects its tag in the 태그 branch; other searches aren't in the tree.
            var tagNode = search.Kind == SearchKind.Tag
                ? _tagsNode?.Children.FirstOrDefault(n => string.Equals(n.TagName, search.Query, StringComparison.CurrentCultureIgnoreCase))
                : null;
            if (tagNode != null)
            {
                _tagsNode!.IsExpanded = true;
                SelectPath(tagNode, new List<FolderNode> { _tagsNode, tagNode });
            }
            else
                SelectPath(null, new List<FolderNode>());
            return;
        }

        // Browsing through a favorite or pinned folder: keep following the path inside that branch.
        // Only once the location leaves that folder does the tree switch to 내 PC.
        if (Anchor != null && Locations.IsSameOrUnder(path, Anchor.FullPath!))
        {
            var anchorChain = Anchor.SelfAndAncestors().Reverse().ToList();
            foreach (var group in anchorChain.Take(anchorChain.Count - 1))
                group.IsExpanded = true;
            SelectPath(Descend(Anchor, RelativePath(path, Anchor), anchorChain), anchorChain);
            return;
        }
        Anchor = null;

        var chain = ChainToStart(path);
        if (chain.Count == 0 || chain[^1].FullPath == null)
        {
            SelectPath(null, chain);   // the network's computers not found yet: the line goes as far as 네트워크
            return;
        }
        SelectPath(Descend(chain[^1], RelativePath(path, chain[^1]), chain), chain);
    }

    static string RelativePath(string path, FolderNode folder) => path[folder.FullPath!.TrimEnd('\\').Length..];

    /// <summary>
    /// The nodes down to where <paramref name="path"/> starts, expanded: 내 PC and its drive, Linux and its
    /// distribution, or 네트워크 and the computer. Empty if the tree has no such start. For a network path whose
    /// computer isn't listed yet it ends at 네트워크, which goes looking (the tree follows once it is found).
    /// </summary>
    List<FolderNode> ChainToStart(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        string? server = Locations.ServerOf(path);
        foreach (var (group, start) in new[] { (_pcNode, root), (_linuxNode, root), (_networkNode, server) })
        {
            if (group == null || start == null)
                continue;
            if (group.Children.FirstOrDefault(n => string.Equals(n.FullPath?.TrimEnd('\\'), start.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is { } node)
            {
                group.IsExpanded = true;
                return new List<FolderNode> { group, node };
            }
        }
        if (server != null && _networkNode != null)
        {
            _networkNode.IsExpanded = true;
            return new List<FolderNode> { _networkNode };
        }
        return new List<FolderNode>();
    }

    /// <summary>
    /// Expands from <paramref name="start"/> along the relative path, appending each node found to
    /// <paramref name="chain"/>. Returns the last node, or null if the tree doesn't list the whole path.
    /// </summary>
    static FolderNode? Descend(FolderNode start, string relativePath, List<FolderNode> chain)
    {
        var node = start;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return null;   // e.g. a hidden folder that the tree doesn't list
            node = next;
            chain.Add(node);
        }
        return node;
    }

    /// <summary>The favorite folder node (in the 즐겨찾기 branch) for a path, else the pinned folder's, if there is one.</summary>
    FolderNode? FindAnchor(string path)
    {
        IEnumerable<FolderNode> Walk(FolderNode group) => group.Children
            .Where(c => c.Favorite != null)
            .SelectMany(c => c.Favorite!.IsGroup ? Walk(c) : new[] { c });
        var favorites = _favoritesNode == null ? Enumerable.Empty<FolderNode>() : Walk(_favoritesNode);
        return favorites.Concat(_pinnedNodes).FirstOrDefault(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="node">
    /// Null when the tree doesn't list the location. Never the nearest node instead: the view navigates to what is
    /// selected, away from the location, and the tree's file commands would act on that other folder.
    /// </param>
    void SelectPath(FolderNode? node, List<FolderNode> chain)
    {
        if (_selected != null && _selected != node)
            _selected.IsSelected = false;
        _selected = node;
        if (node != null)
            node.IsSelected = true;
        MarkPath(chain);
        BringIntoViewRequested?.Invoke(chain);
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

    /// <summary>Favorites changed: rebuild their branch, keeping the anchor on the same favorite if it still exists.</summary>
    public void RebuildFavorites(string location)
    {
        if (_favoritesNode == null)
            return;
        if (_selected?.Favorite != null)
            _selected = null;
        _favoritesNode.Children.Clear();
        foreach (var node in FavoriteNodes())
            _favoritesNode.Children.Add(node);
        _favoritesNode.IsExpanded = true;
        Probing = Task.WhenAll(Probing, ProbeFavoritesAsync());
        if (Anchor != null)
        {
            Anchor = FindAnchor(Anchor.FullPath!);
            Sync(location);
        }
    }

    /// <summary>Memos changed: refresh the tag list and counts, keeping the branch open/closed and the selection.</summary>
    public void RebuildTags(string location)
    {
        if (_tagsNode == null)
            return;
        var updated = TagNodes().ToList();
        if (_tagsNode.Children.Select(n => n.Name).SequenceEqual(updated.Select(n => n.Name)))
            return;
        bool tagSelected = _selected?.TagName != null;
        Quietly(() =>
        {
            _tagsNode.Children.Clear();
            foreach (var node in updated)
                _tagsNode.Children.Add(node);
        });
        if (tagSelected)
            Sync(location);
    }

    /// <summary>
    /// A list showing <paramref name="path"/> has just read it: the tree's nodes for that folder (under 내 PC, and
    /// under 즐겨찾기 if it is a favorite) list the same <paramref name="subfolders"/>, so folders made, deleted or
    /// renamed there — by FileStar or another program — show in the tree without reloading it.
    /// Returns true if that took away the selected node or one on the path line: the caller syncs the tree again.
    /// </summary>
    public bool UpdateSubfolders(string path, IReadOnlyCollection<string> subfolders)
    {
        var target = Path.TrimEndingDirectorySeparator(path);
        var nodes = Roots.SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.IsFolder && string.Equals(Path.TrimEndingDirectorySeparator(n.FullPath!), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selected = _selected;
        // Quietly: if a removed node was selected, the view moves the selection to its parent; that must not navigate.
        Quietly(() =>
        {
            foreach (var node in nodes)
                node.UpdateChildren(subfolders);
        });
        return _selected != selected || _pathChain.Any(n => !InTree(n));
    }

    bool InTree(FolderNode node) =>
        node.Parent is { } parent ? parent.Children.Contains(node) && InTree(parent) : Roots.Contains(node);

    /// <summary>
    /// The location shown isn't in the tree: re-read the folder where the path line stops, in case the location
    /// was made after that folder was read. Returns whether that brought in new folders (the caller syncs again).
    /// </summary>
    public bool RefreshWherePathStops() => _selected == null && _pathChain.LastOrDefault() is { } end && end.RefreshChildren();

    // Children added or removed above the location move the rows the red path line runs past. Where the line
    // stopped, short of the location, the new children may continue it: network and WSL folders are read in the
    // background, so the tree follows a path there one folder at a time.
    void OnChildrenUpdated(FolderNode node)
    {
        if (!_pathChain.Contains(node))
            return;
        if (_selected == null && node == _pathChain[^1] && Locations.IsUnc(_location))
            Sync(_location);
        else
            MarkPath(new List<FolderNode>(_pathChain));
    }

    /// <summary>Memos changed: redraw every folder's marker.</summary>
    public void RefreshMarks()
    {
        foreach (var node in Roots.SelectMany(r => r.SelfAndDescendants()))
            node.RefreshMark();
    }

    // With nothing selected, the node where the path line stops: reloading it may bring the location into the tree.
    FolderNode? ReloadTarget => _selected ?? _pathChain.LastOrDefault();

    /// <summary>Reloads the selected node (and optionally its parent, whose children's names may have changed) without navigating.</summary>
    public void ReloadAround(bool parentToo)
    {
        if (parentToo)
            Quietly(() => ReloadTarget?.Parent?.Reload());
        ReloadTarget?.Reload();
    }

    public void ReloadSelected() => ReloadTarget?.Reload();
}
