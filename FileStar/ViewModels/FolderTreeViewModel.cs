using System.Collections.ObjectModel;
using System.IO;

namespace FileStar.ViewModels;

/// <summary>
/// The folder tree: 즐겨찾기, 내 PC (drives) and 태그. Follows the location being shown — expanding down to it,
/// selecting it and drawing the red path line — and stays inside a 즐겨찾기 branch while browsing below a favorite.
/// </summary>
public sealed class FolderTreeViewModel : ObservableObject
{
    readonly FolderTreeContext _context;
    readonly IFavoritesStore _favorites;
    readonly INotesStore _notes;
    FolderNode? _favoritesNode, _pcNode, _tagsNode, _selected;
    List<FolderNode> _pathChain = new();
    int _quiet;

    public FolderTreeViewModel(FolderTreeContext context, IFavoritesStore favorites, INotesStore notes)
    {
        _context = context;
        _favorites = favorites;
        _notes = notes;
    }

    /// <summary>The user picked a node: show this location.</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>The view should realize and scroll to the last node of this chain (the tree is virtualized).</summary>
    public event Action<IReadOnlyList<FolderNode>>? BringIntoViewRequested;

    public ObservableCollection<FolderNode> Roots { get; } = new();

    public FolderNode? Selected => _selected;

    public FolderNode? FavoritesNode => _favoritesNode;

    /// <summary>The nodes from the root down to the location shown.</summary>
    public IReadOnlyList<FolderNode> PathChain => _pathChain;

    /// <summary>The favorite folder the current location was reached through (the tree stays in 즐겨찾기), or null.</summary>
    public FolderNode? FavoriteAnchor { get; private set; }

    /// <summary>The selected node, if it is a real folder (what keyboard commands on the tree act on).</summary>
    public FolderNode? SelectedFolder => _selected is { IsFolder: true } node ? node : null;

    public void Build()
    {
        Roots.Clear();
        _selected = null;
        _pathChain = new();
        _favoritesNode = FolderNode.Group("즐겨찾기", FavoriteNodes(), ShellInfo.FavoritesIcon);
        Roots.Add(_favoritesNode);
        _pcNode = FolderNode.Group(Locations.ThisPc, DriveInfo.GetDrives().Select(d => FolderNode.Drive(_context, d)));
        Roots.Add(_pcNode);
        // Tags start folded: there can be many.
        _tagsNode = FolderNode.Group("태그", TagNodes(), ShellInfo.TagIcon);
        _tagsNode.IsExpanded = false;
        Roots.Add(_tagsNode);
        if (FavoriteAnchor != null)
            FavoriteAnchor = FindFavoriteFolder(FavoriteAnchor.FullPath!);
    }

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
        // Remember whether this node is inside a favorite, so later moves stay in that branch.
        FavoriteAnchor = node.SelfAndAncestors().FirstOrDefault(n => n.Favorite is { IsGroup: false });
        if (!node.IsVirtual)
            NavigateRequested?.Invoke(node.FullPath!);
        else if (node == _pcNode)
            NavigateRequested?.Invoke(Locations.ThisPc);
    }

    /// <summary>Going to a favorite: keep the tree inside its 즐겨찾기 branch.</summary>
    public void AnchorToFavorite(string path) => FavoriteAnchor = FindFavoriteFolder(path);

    public void ClearAnchor() => FavoriteAnchor = null;

    /// <summary>Expands the tree down to <paramref name="path"/> and selects it.</summary>
    public void Sync(string path)
    {
        if (_pcNode == null)
            return;
        if (Locations.IsThisPc(path))
        {
            FavoriteAnchor = null;
            SelectPath(_pcNode, new List<FolderNode> { _pcNode });
            return;
        }
        if (SearchLocation.TryParse(path) is { } search)
        {
            FavoriteAnchor = null;
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
            {
                if (_selected != null)
                    _selected.IsSelected = false;
                _selected = null;
                MarkPath(new List<FolderNode>());
            }
            return;
        }

        // Browsing through a favorite: keep following the path inside that 즐겨찾기 branch.
        // Only once the location leaves the favorite folder does the tree switch to 내 PC.
        if (FavoriteAnchor != null && Locations.IsSameOrUnder(path, FavoriteAnchor.FullPath!))
        {
            var favoriteChain = FavoriteAnchor.SelfAndAncestors().Reverse().ToList();
            foreach (var group in favoriteChain.Take(favoriteChain.Count - 1))
                group.IsExpanded = true;
            SelectPath(Descend(FavoriteAnchor, path[FavoriteAnchor.FullPath!.TrimEnd('\\').Length..], favoriteChain), favoriteChain);
            return;
        }
        FavoriteAnchor = null;

        string root = Path.GetPathRoot(path) ?? "";
        var drive = _pcNode.Children.FirstOrDefault(n => string.Equals(n.FullPath, root, StringComparison.OrdinalIgnoreCase));
        if (drive == null)
        {
            MarkPath(new List<FolderNode>());
            return;
        }
        _pcNode.IsExpanded = true;
        var chain = new List<FolderNode> { _pcNode, drive };
        SelectPath(Descend(drive, path[root.Length..], chain), chain);
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

    /// <summary>The favorite folder node (in the 즐겨찾기 branch) for a path, if there is one.</summary>
    FolderNode? FindFavoriteFolder(string path)
    {
        IEnumerable<FolderNode> Walk(FolderNode group) => group.Children
            .Where(c => c.Favorite != null)
            .SelectMany(c => c.Favorite!.IsGroup ? Walk(c) : new[] { c });
        return _favoritesNode == null ? null
            : Walk(_favoritesNode).FirstOrDefault(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    void SelectPath(FolderNode node, List<FolderNode> chain)
    {
        if (_selected != null && _selected != node)
            _selected.IsSelected = false;
        _selected = node;
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
        if (FavoriteAnchor != null)
        {
            FavoriteAnchor = FindFavoriteFolder(FavoriteAnchor.FullPath!);
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

    /// <summary>Memos changed: redraw every folder's marker.</summary>
    public void RefreshMarks()
    {
        foreach (var node in Roots.SelectMany(r => r.SelfAndDescendants()))
            node.RefreshMark();
    }

    /// <summary>Reloads the selected node (and optionally its parent, whose children's names may have changed) without navigating.</summary>
    public void ReloadAround(bool parentToo)
    {
        if (parentToo)
            Quietly(() => _selected?.Parent?.Reload());
        _selected?.Reload();
    }

    public void ReloadSelected() => _selected?.Reload();
}
