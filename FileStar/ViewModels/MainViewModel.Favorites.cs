namespace FileStar.ViewModels;

// 즐겨찾기: the menu, the star button, and adding, renaming and removing favorites and their groups.
public sealed partial class MainViewModel
{
    IReadOnlyList<MenuItemViewModel> _favoritesMenu = Array.Empty<MenuItemViewModel>();
    bool _isFavorite;

    /// <summary>The 즐겨찾기 menu: add/remove and manage, then every favorite (the first nine with Ctrl+1…9).</summary>
    public IReadOnlyList<MenuItemViewModel> FavoritesMenu
    {
        get => _favoritesMenu;
        private set => Set(ref _favoritesMenu, value);
    }

    /// <summary>The active list's folder is a favorite (the star is lit).</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        private set => Set(ref _isFavorite, value);
    }

    public bool CanFavorite => !Locations.IsVirtual(ActiveFolder);

    void OnFavoritesChanged()
    {
        Tree.RebuildFavorites(_active.FullPath);
        RebuildFavoritesMenu();
        UpdateFavoriteState();
    }

    void UpdateFavoriteState()
    {
        IsFavorite = _s.Favorites.Contains(ActiveFolder);
        OnPropertyChanged(nameof(CanFavorite));
        OnPropertyChanged(nameof(CanCreateFolder));
    }

    void RebuildFavoritesMenu()
    {
        var menu = new List<MenuItemViewModel>
        {
            MenuItemViewModel.Action("현재 폴더를 즐겨찾기에 추가/제거", () => _s.Favorites.Toggle(_active.FullPath), gesture: "Ctrl+B"),
            MenuItemViewModel.Action("즐겨찾기 관리...", () => ManageFavorites()),
        };
        if (_s.Favorites.Items.Count > 0)
            menu.Add(MenuItemViewModel.Separator);
        int shortcut = 0;
        menu.AddRange(FavoriteEntries(_s.Favorites.Items, ref shortcut));
        FavoritesMenu = menu;
    }

    List<MenuItemViewModel> FavoriteEntries(IEnumerable<Favorite> favorites, ref int shortcut)
    {
        var entries = new List<MenuItemViewModel>();
        foreach (var fav in favorites)
        {
            if (fav.IsGroup)
            {
                var children = FavoriteEntries(fav.Children, ref shortcut);
                if (children.Count == 0)
                    children.Add(MenuItemViewModel.Note("(비어 있음)"));
                entries.Add(new MenuItemViewModel { Header = fav.Name, Icon = ShellInfo.GetIcon("", true), Children = children });
                continue;
            }
            shortcut++;
            string path = fav.FullPath!;
            entries.Add(new MenuItemViewModel
            {
                Header = fav.Name,
                Icon = ShellInfo.GetIcon(path, true, exact: true),
                ToolTip = path,
                Gesture = shortcut <= 9 ? $"Ctrl+{shortcut}" : "",
                Command = new RelayCommand(() => NavigateToFavorite(path)),
            });
        }
        return entries;
    }

    /// <summary>Goes to a favorite and keeps the tree inside its 즐겨찾기 branch.</summary>
    void NavigateToFavorite(string path)
    {
        if (IsDualActive)
        {
            Dual.Navigate(path);
            return;
        }
        Tree.AnchorToFavorite(path);
        if (string.Equals(Locations.Normalize(path), _active.FullPath, StringComparison.OrdinalIgnoreCase))
            Tree.Sync(_active.FullPath);   // already there: just move the tree selection
        else
            Navigate(path);
    }

    /// <summary>Ctrl+1…9.</summary>
    public void OpenFavorite(int index)
    {
        if (_s.Favorites.Folders.ElementAtOrDefault(index) is Favorite fav)
            NavigateToFavorite(fav.FullPath!);
    }

    /// <summary>Ctrl+B: the active list's folder.</summary>
    public void ToggleFavorite() => _s.Favorites.Toggle(ActiveFolder);

    /// <summary>
    /// The star button: toggles right away, or — when adding and groups exist — returns the choice of where to
    /// put it, for the view to show as a menu.
    /// </summary>
    public IReadOnlyList<MenuItemViewModel>? FavoriteButtonClicked()
    {
        string path = ActiveFolder;
        if (_s.Favorites.Contains(path) || !_s.Favorites.Groups().Any())
        {
            _s.Favorites.Toggle(path);
            return null;
        }
        return AddToGroupChoices(path);
    }

    /// <summary>Menu entries for "add <paramref name="path"/> to which group?".</summary>
    List<MenuItemViewModel> AddToGroupChoices(string path)
    {
        var choices = new List<MenuItemViewModel>
        {
            MenuItemViewModel.Action("맨 위 단계", () => _s.Favorites.Add(path)),
            MenuItemViewModel.Separator,
        };
        foreach (var (group, label) in _s.Favorites.Groups())
            choices.Add(new MenuItemViewModel
            {
                Header = label,
                Icon = ShellInfo.GetIcon("", true),
                Command = new RelayCommand(() => _s.Favorites.Add(path, group)),
            });
        choices.Add(MenuItemViewModel.Separator);
        choices.Add(MenuItemViewModel.Action("새 그룹을 만들어 추가...", () =>
        {
            if (_s.Dialogs.Ask("새 그룹", "그룹 이름:", "새 그룹") is string name)
                _s.Favorites.Add(path, _s.Favorites.AddGroup(name));
        }));
        return choices;
    }

    /// <summary>"즐겨찾기에서 제거", or "즐겨찾기에 추가" (with a group submenu once groups exist).</summary>
    MenuItemViewModel FavoriteMenuItem(string path, string prefix = "")
    {
        if (_s.Favorites.Contains(path))
            return MenuItemViewModel.Action(prefix + "즐겨찾기에서 제거", () => _s.Favorites.Remove(path));
        if (!_s.Favorites.Groups().Any())
            return MenuItemViewModel.Action(prefix + "즐겨찾기에 추가", () => _s.Favorites.Add(path), !Locations.IsVirtual(path));
        return new MenuItemViewModel { Header = prefix + "즐겨찾기에 추가", Children = AddToGroupChoices(path), IsEnabled = !Locations.IsVirtual(path) };
    }

    /// <summary>The file list menu's favorite entry: for the selected folder, or the current one.</summary>
    public MenuItemViewModel FileFavoriteMenuItem()
    {
        string folder = FileList.Selected is [{ IsDirectory: true } item] ? item.FullPath : _active.FullPath;
        return FavoriteMenuItem(folder, IsShowing(folder) ? "현재 폴더를 " : "");
    }

    void NewFavoriteGroup(Favorite? parent)
    {
        if (_s.Dialogs.Ask("새 그룹", "그룹 이름:", "새 그룹") is string name)
            _s.Favorites.AddGroup(name, parent);
    }

    void RenameFavorite(Favorite fav)
    {
        if (_s.Dialogs.Ask("이름 바꾸기", fav.IsGroup ? "그룹 이름:" : "트리에 표시할 이름:", fav.Name) is string name)
        {
            fav.Name = name;
            _s.Favorites.Save();
        }
    }

    void DeleteFavorite(Favorite fav)
    {
        int count = fav.SelfAndDescendants().Count() - 1;
        if (count > 0 && !_s.Dialogs.Confirm($"'{fav.Name}' 그룹과 그 안의 항목 {count}개를 즐겨찾기에서 삭제할까요?\n(실제 폴더는 삭제되지 않습니다.)", "그룹 삭제"))
            return;
        _s.Favorites.Remove(fav);
    }

    void ManageFavorites(Favorite? select = null) => _s.Windows.ShowFavoritesManager(_active.FullPath, select);
}
