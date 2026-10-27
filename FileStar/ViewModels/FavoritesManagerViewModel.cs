using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>즐겨찾기 관리: edits a working copy of the favorites tree; nothing is saved unless the user presses 확인.</summary>
public sealed class FavoritesManagerViewModel : ObservableObject
{
    readonly IFavoritesStore _store;
    readonly IDialogService _dialogs;
    readonly string _currentPath;
    Favorite? _selected;

    /// <param name="select">A store item to select initially (e.g. the one being renamed).</param>
    public FavoritesManagerViewModel(IFavoritesStore store, IDialogService dialogs, string currentPath, Favorite? select = null)
    {
        _store = store;
        _dialogs = dialogs;
        _currentPath = currentPath;
        Items = new(store.Items.Select(f => f.Clone()));

        var initial = select == null ? null : AllItems.FirstOrDefault(f => f.Origin == select);
        StartWithName = initial != null;
        Select(initial ?? Items.FirstOrDefault());

        NewGroupCommand = new RelayCommand(NewGroup);
        AddCurrentCommand = new RelayCommand(() => AddFolder(_currentPath), () => !Contains(_currentPath));
        AddBrowseCommand = new RelayCommand(AddBrowse);
        BrowsePathCommand = new RelayCommand(BrowsePath, () => IsFolderSelected);
        RemoveCommand = new RelayCommand(() => Remove(_selected!), () => _selected != null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => Move(1), () => CanMove(1));
        OutdentCommand = new RelayCommand(() => Outdent(_selected!), () => Location(_selected)?.Owner != null);
    }

    public ObservableCollection<Favorite> Items { get; }

    /// <summary>Opened to rename one item: start in its name box rather than the tree.</summary>
    public bool StartWithName { get; }

    public ICommand NewGroupCommand { get; }
    public ICommand AddCurrentCommand { get; }
    public ICommand AddBrowseCommand { get; }
    public ICommand BrowsePathCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OutdentCommand { get; }

    /// <summary>The item being edited below the tree (the view reports the tree's selection).</summary>
    public Favorite? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsFolderSelected));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => _selected != null;

    /// <summary>Only folders have a path to edit.</summary>
    public bool IsFolderSelected => _selected is { IsGroup: false };

    IEnumerable<Favorite> AllItems => Items.SelectMany(f => f.SelfAndDescendants());

    bool Contains(string path) => AllItems.Any(f => !f.IsGroup && string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));

    (ObservableCollection<Favorite> List, Favorite? Owner)? Location(Favorite? item) =>
        item == null ? null : FavoriteTree.Locate(Items, item);

    void Select(Favorite? item)
    {
        if (item == null)
            return;
        // Expand every ancestor so the item's container exists.
        for (var loc = Location(item); loc?.Owner is Favorite owner; loc = Location(owner))
            owner.IsExpanded = true;
        item.IsSelected = true;
        Selected = item;
    }

    /// <summary>New items go inside the selected group, or right after the selected favorite.</summary>
    void Insert(Favorite item)
    {
        if (_selected is { IsGroup: true } group)
        {
            group.Children.Add(item);
            group.IsExpanded = true;
        }
        else if (Location(_selected) is { } loc)
            loc.List.Insert(loc.List.IndexOf(_selected!) + 1, item);
        else
            Items.Add(item);
        Select(item);
    }

    void AddFolder(string path)
    {
        var existing = AllItems.FirstOrDefault(f => !f.IsGroup && string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            Select(existing);
        else
            Insert(Favorite.Folder(path));
    }

    void NewGroup()
    {
        if (_dialogs.Ask("새 그룹", "그룹 이름:", "새 그룹") is string name)
            Insert(Favorite.Group(name));
    }

    void AddBrowse()
    {
        if (_dialogs.PickFolder("즐겨찾기에 추가할 폴더 선택", _currentPath) is string path)
            AddFolder(Locations.Normalize(path));
    }

    void BrowsePath()
    {
        if (_selected is { IsGroup: false } fav && _dialogs.PickFolder("즐겨찾기에 추가할 폴더 선택", fav.FullPath) is string path)
            fav.FullPath = Locations.Normalize(path);
    }

    bool CanMove(int step) =>
        Location(_selected) is { } loc && loc.List.IndexOf(_selected!) + step is var target && target >= 0 && target < loc.List.Count;

    public void Move(int step)
    {
        if (!CanMove(step) || Location(_selected) is not { } loc)
            return;
        var item = _selected!;
        int index = loc.List.IndexOf(item);
        loc.List.Move(index, index + step);
        Select(item);
    }

    /// <summary>Moves <paramref name="item"/> out of its group, placing it right after that group.</summary>
    public void Outdent(Favorite item)
    {
        if (Location(item) is not { Owner: Favorite owner } loc || Location(owner) is not { } ownerLoc)
            return;
        loc.List.Remove(item);
        ownerLoc.List.Insert(ownerLoc.List.IndexOf(owner) + 1, item);
        Select(item);
    }

    public void Remove(Favorite item)
    {
        if (item.IsGroup && item.Children.Count > 0 &&
            !_dialogs.Confirm($"'{item.Name}' 그룹과 그 안의 즐겨찾기 {item.SelfAndDescendants().Count() - 1}개를 모두 삭제할까요?", "그룹 삭제"))
            return;
        if (Location(item) is not { } loc)
            return;
        int index = loc.List.IndexOf(item);
        loc.List.RemoveAt(index);
        var next = loc.List.Count > 0 ? loc.List[Math.Min(index, loc.List.Count - 1)] : loc.Owner;
        Selected = null;
        Select(next);
    }

    // ── Drag & drop ──

    /// <summary>A group can't be dropped onto itself or into its own descendants.</summary>
    public static bool CanDrop(Favorite source, Favorite? target) =>
        target != source && (target == null || !source.IsAncestorOf(target));

    /// <summary>Onto a group: into it. Onto another item: in front of it. Onto empty space: the end of the top level.</summary>
    public void Drop(Favorite source, Favorite? target)
    {
        if (!CanDrop(source, target) || Location(source) is not { } from)
            return;
        from.List.Remove(source);
        if (target == null)
            Items.Add(source);
        else if (target.IsGroup)
        {
            target.Children.Add(source);
            target.IsExpanded = true;
        }
        else if (Location(target) is { } to)
            to.List.Insert(to.List.IndexOf(target), source);
        else
            Items.Add(source);
        Select(source);
    }

    /// <summary>확인: tidies names and paths, drops folders left without a path, and saves.</summary>
    public void Accept()
    {
        foreach (var fav in AllItems.ToList())
        {
            if (!fav.IsGroup)
                fav.FullPath = fav.FullPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(fav.Name))
                fav.Name = fav.IsGroup ? "그룹" : Locations.DisplayName(fav.FullPath!);
        }
        FavoriteTree.RemoveWhere(Items, f => !f.IsGroup && string.IsNullOrEmpty(f.FullPath));
        foreach (var fav in AllItems)
            fav.IsSelected = false;
        _store.Replace(Items);
    }
}
