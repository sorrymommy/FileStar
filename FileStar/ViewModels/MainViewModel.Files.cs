using System.IO;

namespace FileStar.ViewModels;

// Commands on the main list's selection. Copies, moves, deletes and renames go through IFileOperations (Windows'
// own engine: same clipboard format, Recycle Bin, progress and conflict dialogs as Explorer), batch renames through
// BatchRenamer and zips through ZipTools; each carries the memos along and records what Ctrl+Z needs.
public sealed partial class MainViewModel
{
    FileItem? _renamingItem;

    List<FileItem> Selected => FileList.Selected.ToList();
    List<string> SelectedPaths => FileList.Selected.Select(i => i.FullPath).ToList();
    bool AnySelected => FileList.Selected.Count > 0;

    /// <summary>In 내 PC the rows are drives and the user's main folders: open, browse and inspect only.</summary>
    bool Editable => !InThisPc;

    // ── Context-menu state (raised when the menu opens) ──

    public bool CanUnzip => FileList.Selected is [{ IsZip: true }];
    public bool CanOpenLocation => CurrentSearch != null && FileList.Selected.Count == 1;
    public string ShowInExplorerHeader => AnySelected ? "Windows 탐색기에서 보기" : "현재 폴더를 Windows 탐색기에서 열기";
    public string CopyPathHeader => AnySelected ? "경로 복사" : "현재 폴더 경로 복사";

    /// <summary>The list's right-click menu is opening: bring its headers and visibility up to date.</summary>
    public void RefreshFileMenu()
    {
        foreach (var name in new[] { nameof(CanUnzip), nameof(CanOpenLocation), nameof(ShowInExplorerHeader), nameof(CopyPathHeader),
                     nameof(DualMenuVisible), nameof(CopyToDualHeader), nameof(MoveToDualHeader) })
            OnPropertyChanged(name);
    }

    // ── Opening ──

    public void OpenSelected()
    {
        var items = Selected;
        if (items is [{ IsDirectory: true } folder])
        {
            Navigate(folder.FullPath);
            return;
        }
        foreach (var item in items)
        {
            if (item.IsDirectory)
                NewTab(item.FullPath);
            else
                ShellOpen(item.FullPath);
        }
    }

    public void ShellOpen(string path)
    {
        try
        {
            _s.Shell.Open(path);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn(ex.Message, "열 수 없음");
        }
    }

    public void OpenSelectedFoldersInNewTabs()
    {
        foreach (var item in Selected.Where(i => i.IsDirectory))
            NewTab(item.FullPath);
    }

    void OpenItemLocation()
    {
        if (FileList.Selected.FirstOrDefault() is FileItem item)
            Navigate(item.FolderPath, item.Name);
    }

    void ShowInExplorer()
    {
        if (FileList.Selected.FirstOrDefault() is FileItem item)
            _s.Shell.OpenInExplorer(item.FullPath, select: true);
        else
            _s.Shell.OpenInExplorer(_active.FullPath, select: false);
    }

    public void ShowSelectedProperties()
    {
        if (FileList.Selected.FirstOrDefault() is FileItem item)
            _s.Shell.ShowProperties(item.FullPath);
    }

    public void CopySelectedPaths()
    {
        var paths = SelectedPaths;
        _s.Shell.CopyText(paths.Count > 0 ? string.Join(Environment.NewLine, paths) : _active.FullPath);
    }

    // ── Clipboard, delete ──

    public void ClipboardSelected(bool cut)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0 || InThisPc)
            return;
        try
        {
            _s.FileOps.SetClipboard(paths, cut);
            Status.Show($"{paths.Count:N0}개 항목을 " + (cut ? "잘라냈습니다. 붙여넣으면 이동됩니다." : "복사했습니다."));
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn((cut ? "잘라내기" : "복사") + "할 수 없습니다: " + ex.Message);
        }
    }

    public async Task PasteIntoCurrentFolderAsync()
    {
        if (InVirtual)
            return;
        string folder = _active.FullPath;
        try
        {
            if (await _s.FileOps.PasteAsync(folder) && IsShowing(folder))
                await LoadFilesAsync(folder, FileList.Selected.Select(i => i.Name).ToList());
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("붙여넣을 수 없습니다: " + ex.Message);
        }
    }

    /// <summary>Delete → Recycle Bin, Shift+Delete → permanent; confirmations follow the Windows settings.</summary>
    public async Task DeleteSelectedAsync(bool permanently)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0 || InThisPc)   // never offer to delete a drive or a user folder from 내 PC
            return;
        // Keep the cursor near where it was: select the row now at the same position.
        var visible = FileList.VisibleItems.ToList();
        int index = visible.IndexOf(FileList.Selected[0]);
        string folder = _active.FullPath;
        try
        {
            if (!await _s.FileOps.DeleteAsync(paths, permanently))
                return;   // cancelled or failed (Windows has already said why)
            if (!IsShowing(folder))
                return;
            await LoadFilesAsync(folder);
            var rows = FileList.VisibleItems.ToList();
            if (rows.Count > 0)
            {
                FileList.Select(rows[Math.Clamp(index, 0, rows.Count - 1)]);
                Focus(FocusTarget.FileList);
            }
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("삭제할 수 없습니다: " + ex.Message);
        }
    }

    bool IsShowing(string folder) => string.Equals(folder, _active.FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Files dropped on the list, a folder row or a tree folder: copied or moved there.</summary>
    public async Task DropAsync(IReadOnlyList<string> files, string folder, bool move)
    {
        try
        {
            if (await _s.FileOps.TransferAsync(files, folder, move))
            {
                if (!InThisPc)
                    await LoadFilesAsync(_active.FullPath, files.Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList());
                Tree.ReloadSelected();
                Tree.Sync(_active.FullPath);
            }
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn((move ? "옮길" : "복사할") + " 수 없습니다: " + ex.Message);
        }
    }

    // ── New folder ──

    /// <summary>Ctrl+Shift+N, the toolbar button, the menus: a new folder in the list being used.</summary>
    public void NewFolder()
    {
        if (IsDualActive)
            _ = Dual.NewFolderAsync();
        else
            _ = CreateNewFolderAsync();
    }

    public bool CanCreateFolder => !Locations.IsVirtual(ActiveFolder);

    /// <summary>"새 폴더" (or "새 폴더 (2)" …) in the current folder, or in <paramref name="parent"/> (the tree's menu,
    /// which goes there first), then rename it in place.</summary>
    async Task CreateNewFolderAsync(string? parent = null)
    {
        if (parent != null && !IsShowing(parent))
            Navigate(parent);
        if (InVirtual)
            return;
        string name = Locations.NewFolderName(_active.FullPath);
        try
        {
            _s.FileOps.CreateFolder(_active.FullPath, name);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("새 폴더를 만들 수 없습니다: " + ex.Message);
            return;
        }
        await LoadFilesAsync(_active.FullPath, new[] { name });
        if (FileList.AllItems.FirstOrDefault(i => i.Name == name) is FileItem created)
            BeginRename(created);
    }

    // ── In-place rename (F2) ──

    public void BeginRename(FileItem item)
    {
        if (InThisPc)
            return;
        if (_renamingItem != null)
            _renamingItem.IsRenaming = false;
        _renamingItem = item;
        FileList.Select(item);
        item.IsRenaming = true;   // the template swaps the name for an edit box, which the view focuses
    }

    public void RenameSelected()
    {
        if (FileList.Selected is [var single])
            BeginRename(single);
    }

    public void CancelRename(FileItem item) => EndRename(item, focusRow: true);

    void EndRename(FileItem item, bool focusRow)
    {
        item.IsRenaming = false;
        if (_renamingItem == item)
            _renamingItem = null;
        if (focusRow)
            Focus(FocusTarget.FileList);
        if (_reloadAfterRename && _renamingItem == null)
        {
            _reloadAfterRename = false;
            Refresh(reloadTree: false);
        }
    }

    /// <param name="next">Rename the row this many places away once this one is done (↑/↓ while renaming), or 0.</param>
    public void CommitRename(FileItem item, string newName, int next = 0)
    {
        if (!item.IsRenaming)
            return;   // already handled (Enter, then the focus loss that follows)
        string? thenRename = next == 0 ? null : FileList.NeighborOf(item, next)?.Name;
        if (next != 0 && thenRename == null)
            return;   // no row there: keep editing this one
        newName = newName.Trim();
        EndRename(item, focusRow: true);
        if (newName.Length == 0 || newName == item.Name)
        {
            if (thenRename != null)
                BeginRenameByName(thenRename);
            return;
        }
        if (FileNames.HasInvalidChars(newName))
        {
            _s.Dialogs.Warn(FileNames.InvalidCharsMessage);
            BeginRename(item);
            return;
        }
        _ = MoveToNewNameAsync(item.FullPath, newName, thenRename);
    }

    void BeginRenameByName(string name)
    {
        if (FileList.FindByName(name) is FileItem row)
            BeginRename(row);
    }

    /// <summary>Renames on disk, keeps memo/tags attached, and reloads the list with the renamed item selected.</summary>
    async Task MoveToNewNameAsync(string path, string newName, string? thenRename = null)
    {
        try
        {
            _s.FileOps.Rename(path, newName);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("이름을 바꿀 수 없습니다: " + ex.Message);
            return;
        }
        // The reload replaces every row, so the next row to edit is found again by name.
        await LoadFilesAsync(_active.FullPath, new[] { thenRename ?? newName });
        if (thenRename != null)
            BeginRenameByName(thenRename);
    }

    /// <summary>Rename through a dialog, for paths not shown as a row (e.g. a tree folder from the Explorer menu).</summary>
    void RenameWithDialog(string path)
    {
        string oldName = Path.GetFileName(path.TrimEnd('\\'));
        if (_s.Dialogs.Ask("이름 바꾸기", "새 이름:", oldName) is not string newName || newName == oldName)
            return;
        if (FileNames.HasInvalidChars(newName))
        {
            _s.Dialogs.Warn(FileNames.InvalidCharsMessage);
            return;
        }
        _ = MoveToNewNameAsync(path, newName);
    }

    /// <summary>Explorer-menu verbs that only work inside Explorer's own window; true if handled here.</summary>
    public bool HandleShellVerb(string verb, IReadOnlyList<string> paths)
    {
        if (!verb.Equals("rename", StringComparison.OrdinalIgnoreCase) || paths.Count != 1)
            return false;
        // In the list: edit the name in place. Elsewhere (e.g. a tree folder): ask in a dialog.
        if (FileList.Find(paths[0]) is FileItem row)
            BeginRename(row);
        else
            RenameWithDialog(paths[0]);
        return true;
    }

    // ── 이름 한꺼번에 바꾸기 ──

    void BatchRename()
    {
        // In list order, so numbering follows what the user sees.
        var selected = FileList.Selected.ToHashSet();
        var items = FileList.VisibleItems.Where(selected.Contains).ToList();
        if (items.Count < 2 || InThisPc)
            return;
        if (_s.Windows.AskBatchRename(items) is not { Count: > 0 } plan)
            return;
        var (done, failed) = BatchRenamer.Apply(plan, _s.Notes);
        if (done.Count > 0)
            _s.Undo.Push(new BatchRenameUndo(done));
        _ = LoadFilesAsync(_active.FullPath, plan.Select(p => p.NewName).ToList());
        Status.Note($"이름 {done.Count:N0}개를 바꿨습니다 (Ctrl+Z로 되돌리기)", FileList);
        if (failed.Count > 0)
            _s.Dialogs.Warn("바꾸지 못한 항목:\n" + string.Join("\n", failed.Take(15)));
    }

    // ── zip ──

    async Task ZipSelectedAsync()
    {
        var items = Selected;
        if (items.Count == 0 || InThisPc)
            return;
        string folder = items[0].FolderPath;
        string baseName = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : new DirectoryInfo(folder).Name;
        string zipPath = ZipTools.FreeName(folder, baseName, ".zip");
        var paths = items.Select(i => i.FullPath).ToList();
        Status.Show($"'{Path.GetFileName(zipPath)}' 만드는 중...");
        try
        {
            await Task.Run(() => ZipTools.Create(zipPath, paths, CancellationToken.None));
        }
        catch (Exception ex)
        {
            try { File.Delete(zipPath); } catch { }
            _s.Dialogs.Warn("압축하지 못했습니다: " + ex.Message);
            return;
        }
        _s.Undo.Push(new CreateUndo(new[] { zipPath }, "압축"));
        if (!InVirtual)
            await LoadFilesAsync(_active.FullPath, new[] { Path.GetFileName(zipPath) });
        Status.Note($"'{Path.GetFileName(zipPath)}'로 압축했습니다", FileList);
    }

    async Task UnzipSelectedAsync()
    {
        if (FileList.Selected is not [{ IsZip: true } zip])
            return;
        string target = ZipTools.FreeName(zip.FolderPath, Path.GetFileNameWithoutExtension(zip.Name), "");
        Status.Show($"'{zip.Name}' 푸는 중...");
        try
        {
            await Task.Run(() => ZipTools.Extract(zip.FullPath, target));
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("압축을 풀지 못했습니다: " + ex.Message);
            return;
        }
        _s.Undo.Push(new CreateUndo(new[] { target }, "압축 풀기"));
        if (!InVirtual)
            await LoadFilesAsync(_active.FullPath, new[] { Path.GetFileName(target) });
        Status.Note($"'{Path.GetFileName(target)}' 폴더에 풀었습니다", FileList);
    }
}
