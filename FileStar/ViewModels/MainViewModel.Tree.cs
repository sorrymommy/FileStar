using System.IO;

namespace FileStar.ViewModels;

// The same file commands on the folder tree (Ctrl+C/X/V, Del, F2 while the tree has focus, or its menu),
// plus the tree's right-click menu.
public sealed partial class MainViewModel
{
    FolderNode? _renamingNode;

    /// <param name="target">The right-clicked node; keyboard commands use the selected one.</param>
    public void TreeClipboard(bool cut, FolderNode? target = null)
    {
        if ((target ?? Tree.SelectedFolder) is not { IsFolder: true } node || cut && !node.IsMovable)
            return;
        try
        {
            _s.FileOps.SetClipboard(new[] { node.FullPath! }, cut);
            Status.Show($"'{node.Name}' 폴더를 " + (cut ? "잘라냈습니다. 붙여넣으면 이동됩니다." : "복사했습니다."));
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn((cut ? "잘라내기" : "복사") + "할 수 없습니다: " + ex.Message);
        }
    }

    public async Task TreePasteAsync(FolderNode? target = null)
    {
        if ((target ?? Tree.SelectedFolder) is not { IsFolder: true } node)
            return;
        string folder = node.FullPath!;
        try
        {
            if (!await _s.FileOps.PasteAsync(folder))
                return;
            node.Reload();
            node.IsExpanded = true;
            if (IsShowing(folder))
                await LoadFilesAsync(folder);
            Tree.Sync(_active.FullPath);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("붙여넣을 수 없습니다: " + ex.Message);
        }
    }

    public async Task TreeDeleteAsync(bool permanently, FolderNode? target = null)
    {
        target ??= Tree.Selected;
        if (target?.Favorite is Favorite fav)
        {
            // Favorites and their groups are FileStar's own list: Delete takes them off it, the real folder stays.
            if (fav.IsGroup)
                DeleteFavorite(fav);   // asks first when the group isn't empty
            else if (_s.Dialogs.Confirm($"'{fav.Name}'을(를) 즐겨찾기에서 뺄까요?\n\n실제 폴더는 지워지지 않습니다.", "즐겨찾기"))
                DeleteFavorite(fav);
            return;
        }
        if (target is not { IsMovable: true } node)
            return;
        string path = node.FullPath!;
        string parent = Path.GetDirectoryName(path)!;
        try
        {
            if (!await _s.FileOps.DeleteAsync(new[] { path }, permanently))
                return;   // cancelled or failed (Windows has already said why)
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("삭제할 수 없습니다: " + ex.Message);
            return;
        }
        node.Parent?.Reload();
        // The folder being shown (or one inside it) is gone: step out to the parent.
        if (Locations.IsSameOrUnder(_active.FullPath, path))
            Navigate(parent, Path.GetFileName(path));
        else
        {
            Tree.Sync(_active.FullPath);
            if (IsShowing(parent))
                await LoadFilesAsync(parent);
        }
        Focus(FocusTarget.Tree);
    }

    public void BeginTreeRename(FolderNode? target = null)
    {
        target ??= Tree.Selected;
        if (target?.Favorite is Favorite fav)
        {
            RenameFavorite(fav);   // the display name in 즐겨찾기, not the folder on disk
            return;
        }
        if (target is not { IsMovable: true } node)
            return;
        if (_renamingNode != null && _renamingNode != node)
            _renamingNode.IsRenaming = false;
        _renamingNode = node;
        node.IsRenaming = true;   // the template swaps the name for an edit box, which the view focuses
    }

    public void CancelTreeRename(FolderNode node)
    {
        node.IsRenaming = false;
        if (_renamingNode == node)
            _renamingNode = null;
        Focus(FocusTarget.TreeIfFocused);
    }

    public void CommitTreeRename(FolderNode node, string newName)
    {
        if (!node.IsRenaming)
            return;   // already handled (Enter, then the focus loss that follows)
        newName = newName.Trim();
        string path = node.FullPath!;
        CancelTreeRename(node);
        if (newName.Length == 0 || newName == Path.GetFileName(path))
            return;
        if (FileNames.HasInvalidChars(newName))
        {
            _s.Dialogs.Warn(FileNames.InvalidCharsMessage);
            BeginTreeRename(node);
            return;
        }
        string newPath;
        try
        {
            newPath = _s.FileOps.Rename(path, newName);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("이름을 바꿀 수 없습니다: " + ex.Message);
            return;
        }
        bool showing = Locations.IsSameOrUnder(_active.FullPath, path);
        Tree.Quietly(() => node.Parent?.Reload());
        if (showing)
        {
            // Keep showing the same folder under its new name, without adding a history step.
            _active.FullPath = newPath + _active.FullPath[path.Length..];
            ShowCurrent();
        }
        else
        {
            Tree.Sync(_active.FullPath);
            Refresh(reloadTree: false);
        }
        Focus(FocusTarget.Tree);
    }

    void ReloadTreeNode(FolderNode node)
    {
        node.Reload();
        Tree.Sync(_active.FullPath);   // reloaded nodes need the path marking again
    }

    /// <summary>
    /// The tree's right-click menu for <paramref name="node"/> (null: empty space). The view adds the Windows
    /// "탐색기 메뉴" entry at the end for real folders.
    /// </summary>
    public List<MenuItemViewModel> TreeContextMenu(FolderNode? node)
    {
        var menu = new List<MenuItemViewModel>();
        string? path = node is { IsFolder: true } ? node.FullPath : null;
        if (path != null)
        {
            menu.Add(MenuItemViewModel.Action("새 탭에서 열기", () => NewTab(path)));
            menu.Add(MenuItemViewModel.Action("Windows 탐색기에서 열기", () => _s.Shell.OpenInExplorer(path, select: false)));
            menu.Add(MenuItemViewModel.Action("경로 복사", () => _s.Shell.CopyText(path)));
            menu.Add(MenuItemViewModel.Action("새 폴더", () => _ = CreateNewFolderAsync(path)));
            menu.Add(MenuItemViewModel.Separator);
            menu.Add(FavoriteMenuItem(path));
        }

        if (node?.Favorite is Favorite fav)
        {
            if (fav.IsGroup)
            {
                menu.Add(MenuItemViewModel.Action("이 그룹에 현재 폴더 추가", () => _s.Favorites.Add(_active.FullPath, fav), !_s.Favorites.Contains(_active.FullPath)));
                menu.Add(MenuItemViewModel.Action("하위 그룹 만들기...", () => NewFavoriteGroup(fav)));
                menu.Add(MenuItemViewModel.Action("그룹 이름 바꾸기...", () => RenameFavorite(fav)));
                menu.Add(MenuItemViewModel.Action("그룹 삭제", () => DeleteFavorite(fav)));
            }
            else
                menu.Add(MenuItemViewModel.Action("즐겨찾기 표시 이름 바꾸기...", () => RenameFavorite(fav)));
        }
        else if (node != null && node == Tree.FavoritesNode)
            menu.Add(MenuItemViewModel.Action("새 그룹...", () => NewFavoriteGroup(null)));
        menu.Add(MenuItemViewModel.Action("즐겨찾기 관리...", () => ManageFavorites(node?.Favorite)));

        if (path != null)
        {
            bool movable = node!.IsMovable && node.Favorite == null;
            menu.Add(MenuItemViewModel.Separator);
            menu.Add(MenuItemViewModel.Action("복사 (Ctrl+C)", () => TreeClipboard(cut: false, node)));
            menu.Add(MenuItemViewModel.Action("잘라내기 (Ctrl+X)", () => TreeClipboard(cut: true, node), movable));
            menu.Add(MenuItemViewModel.Action("붙여넣기 (Ctrl+V)", () => _ = TreePasteAsync(node), _s.FileOps.ClipboardHasFiles));
            menu.Add(MenuItemViewModel.Action("삭제 (Del)", () => _ = TreeDeleteAsync(IsShiftDown(), node), movable));
            menu.Add(MenuItemViewModel.Action("이름 바꾸기 (F2)", () => BeginTreeRename(node), movable));
            menu.Add(MenuItemViewModel.Separator);
            menu.Add(MenuItemViewModel.Action("하위 폴더 새로 고침", () => ReloadTreeNode(node)));
            menu.Add(MenuItemViewModel.Action("속성", () => _s.Shell.ShowProperties(path)));
        }
        return menu;
    }
}
