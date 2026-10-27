using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FileStar;

/// <summary>Edits a working copy of the favorites tree; nothing is saved unless the user presses 확인.</summary>
public partial class FavoritesWindow : Window
{
    readonly ObservableCollection<Favorite> _items;
    readonly string _currentPath;
    Point _dragStart;
    Favorite? _dragCandidate;

    /// <param name="select">A store item to select initially (e.g. the one being renamed).</param>
    public FavoritesWindow(string currentPath, Favorite? select = null)
    {
        InitializeComponent();
        _currentPath = currentPath;
        _items = new(FavoritesStore.Items.Select(f => f.Clone()));
        FavoriteTree.ItemsSource = _items;

        var initial = select == null ? null : AllItems.FirstOrDefault(f => f.Origin == select);
        Select(initial ?? _items.FirstOrDefault());
        UpdateButtons();
        Loaded += (_, _) =>
        {
            if (initial != null)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
            else
                FavoriteTree.Focus();
        };
    }

    IEnumerable<Favorite> AllItems => _items.SelectMany(f => f.SelfAndDescendants());

    Favorite? Selected => FavoriteTree.SelectedItem as Favorite;

    void Select(Favorite? item)
    {
        if (item == null)
            return;
        // Expand every ancestor so the item's container exists.
        for (var loc = FavoritesStore.Locate(_items, item); loc?.Owner is Favorite owner; loc = FavoritesStore.Locate(_items, owner))
            owner.IsExpanded = true;
        item.IsSelected = true;
    }

    void UpdateButtons()
    {
        var selected = Selected;
        var location = selected == null ? null : FavoritesStore.Locate(_items, selected);
        EditPanel.DataContext = selected;
        EditPanel.IsEnabled = selected != null;
        bool isFolder = selected is { IsGroup: false };
        PathLabel.IsEnabled = PathBox.IsEnabled = BrowsePathButton.IsEnabled = isFolder;
        RemoveButton.IsEnabled = selected != null;
        UpButton.IsEnabled = location is { } l1 && l1.List.IndexOf(selected!) > 0;
        DownButton.IsEnabled = location is { } l2 && l2.List.IndexOf(selected!) < l2.List.Count - 1;
        OutdentButton.IsEnabled = location?.Owner != null;
        AddCurrentButton.IsEnabled = !AllItems.Any(f => !f.IsGroup && string.Equals(f.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>New items go inside the selected group, or right after the selected favorite.</summary>
    void Insert(Favorite item)
    {
        var selected = Selected;
        if (selected is { IsGroup: true })
        {
            selected.Children.Add(item);
            selected.IsExpanded = true;
        }
        else if (selected != null && FavoritesStore.Locate(_items, selected) is { } loc)
            loc.List.Insert(loc.List.IndexOf(selected) + 1, item);
        else
            _items.Add(item);
        Select(item);
        UpdateButtons();
    }

    void AddFolder(string path)
    {
        var existing = AllItems.FirstOrDefault(f => !f.IsGroup && string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Select(existing);
            return;
        }
        Insert(Favorite.Folder(path));
    }

    static string? PickFolder(string? initial)
    {
        var dialog = new OpenFolderDialog { Title = "즐겨찾기에 추가할 폴더 선택" };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
            dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    void Move(int step)
    {
        if (Selected is not Favorite item || FavoritesStore.Locate(_items, item) is not { } loc)
            return;
        int index = loc.List.IndexOf(item);
        int target = index + step;
        if (target < 0 || target >= loc.List.Count)
            return;
        loc.List.Move(index, target);
        Select(item);
        UpdateButtons();
    }

    /// <summary>Moves <paramref name="item"/> out of its group, placing it right after that group.</summary>
    void Outdent(Favorite item)
    {
        if (FavoritesStore.Locate(_items, item) is not { Owner: Favorite owner } loc ||
            FavoritesStore.Locate(_items, owner) is not { } ownerLoc)
            return;
        loc.List.Remove(item);
        ownerLoc.List.Insert(ownerLoc.List.IndexOf(owner) + 1, item);
        Select(item);
        UpdateButtons();
    }

    void Remove(Favorite item)
    {
        if (item.IsGroup && item.Children.Count > 0 &&
            MessageBox.Show(this, $"'{item.Name}' 그룹과 그 안의 즐겨찾기 {item.SelfAndDescendants().Count() - 1}개를 모두 삭제할까요?",
                "그룹 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (FavoritesStore.Locate(_items, item) is not { } loc)
            return;
        int index = loc.List.IndexOf(item);
        loc.List.RemoveAt(index);
        Select(loc.List.Count > 0 ? loc.List[Math.Min(index, loc.List.Count - 1)] : loc.Owner);
        UpdateButtons();
    }

    // ───────────── Drag & drop ─────────────

    static Favorite? FavoriteFromElement(object source)
    {
        for (var node = source as DependencyObject; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TreeViewItem { DataContext: Favorite fav })
                return fav;
        }
        return null;
    }

    void FavoriteTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FavoriteFromElement(e.OriginalSource);
    }

    void FavoriteTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null)
            return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var item = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(FavoriteTree, item, DragDropEffects.Move);
    }

    void FavoriteTree_DragOver(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(Favorite)) as Favorite;
        var target = FavoriteFromElement(e.OriginalSource);
        // A group can't be dropped onto itself or into its own descendants.
        e.Effects = source == null || target == source || (target != null && source.IsAncestorOf(target))
            ? DragDropEffects.None
            : DragDropEffects.Move;
        e.Handled = true;
    }

    void FavoriteTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Favorite)) is not Favorite source)
            return;
        var target = FavoriteFromElement(e.OriginalSource);
        if (target == source || (target != null && source.IsAncestorOf(target)))
            return;
        if (FavoritesStore.Locate(_items, source) is not { } from)
            return;

        from.List.Remove(source);
        if (target == null)
            _items.Add(source);
        else if (target.IsGroup)
        {
            target.Children.Add(source);
            target.IsExpanded = true;
        }
        else if (FavoritesStore.Locate(_items, target) is { } to)
            to.List.Insert(to.List.IndexOf(target), source);
        else
            _items.Add(source);
        Select(source);
        UpdateButtons();
    }

    // ───────────── Handlers ─────────────

    void FavoriteTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateButtons();

    void FavoriteTree_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Delete && Selected is Favorite item)
            Remove(item);
        else if (key == Key.F2)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
        else if (key == Key.Up && mods == ModifierKeys.Alt)
            Move(-1);
        else if (key == Key.Down && mods == ModifierKeys.Alt)
            Move(1);
        else if (key == Key.Left && mods == ModifierKeys.Alt && Selected is Favorite outItem)
            Outdent(outItem);
        else
            return;
        e.Handled = true;
    }

    void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (InputDialog.Ask(this, "새 그룹", "그룹 이름:", "새 그룹") is string name)
            Insert(Favorite.Group(name));
    }

    void AddCurrent_Click(object sender, RoutedEventArgs e) => AddFolder(_currentPath);

    void AddBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_currentPath) is string path)
            AddFolder(FileSystem.Normalize(path));
    }

    void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsGroup: false } fav && PickFolder(fav.FullPath) is string path)
            fav.FullPath = FileSystem.Normalize(path);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is Favorite item)
            Remove(item);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    void Outdent_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is Favorite item)
            Outdent(item);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var fav in AllItems.ToList())
        {
            if (!fav.IsGroup)
                fav.FullPath = fav.FullPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(fav.Name))
                fav.Name = fav.IsGroup ? "그룹" : FileSystem.DisplayName(fav.FullPath!);
        }
        // Drop folder entries left with an empty path.
        void Prune(ObservableCollection<Favorite> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].IsGroup && string.IsNullOrEmpty(list[i].FullPath))
                    list.RemoveAt(i);
                else
                    Prune(list[i].Children);
            }
        }
        Prune(_items);
        foreach (var fav in AllItems)
            fav.IsSelected = false;
        FavoritesStore.Replace(_items);
        DialogResult = true;
    }
}
