using System.IO;

namespace FileStar.ViewModels;

// 두 창 보기 and 폴더 비교. The two lists are equals: the window's address bar, back/forward/up, tree and
// info pane all serve whichever list is active, and folder compare marks every item against the other list.
public sealed partial class MainViewModel
{
    /// <summary>The folder the main list shows, or null when it shows 내 PC or a result list.</summary>
    public string? MainFolder => InVirtual ? null : _active?.FullPath;

    public bool IsDualVisible
    {
        get => _isDualVisible;
        set => SetDualPane(value);
    }

    /// <summary>The right list is open and had the keyboard last: the window's navigation and info pane serve it.</summary>
    public bool IsDualActive => _isDualActive && _isDualVisible;

    /// <summary>Which list has the keyboard (the view reports focus changes).</summary>
    public void SetActiveList(bool dual)
    {
        if (_isDualActive == dual)
            return;
        _isDualActive = dual;
        OnDualActiveChanged();
        UpdateChrome();
        UpdateInfoPane();
    }

    void OnDualActiveChanged()
    {
        Dual.IsActive = IsDualActive;
        OnPropertyChanged(nameof(IsDualActive));
        OnPropertyChanged(nameof(IsMainPaneHighlighted));
    }

    public void SetDualPane(bool show, string? folder = null)
    {
        if (_isDualVisible == show)
            return;
        if (!show && Compare.IsOn)
            SetCompare(false);
        _isDualVisible = show;
        OnPropertyChanged(nameof(IsDualVisible));
        if (show)
        {
            Dual.List.SetSort(FileList.SortKey, FileList.SortDescending);
            if (!Dual.HasFolder)
            {
                string start = folder != null && Directory.Exists(folder) ? folder
                    : MainFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Dual.Navigate(start);
            }
        }
        else if (_isDualActive)
        {
            SetActiveList(false);
            Focus(FocusTarget.FileList);
        }
        OnDualActiveChanged();
        RequestSave();
    }

    /// <summary>Copies or moves the main list's selection into the right list's folder.</summary>
    async Task SendToDualAsync(bool move)
    {
        var paths = FileList.Selected.Select(i => i.FullPath).ToList();
        if (!_isDualVisible || paths.Count == 0)
            return;
        await Dual.ReceiveAsync(paths, move);
        Refresh(reloadTree: false);
    }

    public bool DualMenuVisible => _isDualVisible && FileList.Selected.Count > 0;

    string DualFolderSuffix => Dual.HasFolder ? $" ({Locations.DisplayName(Dual.Folder)})" : "";
    public string CopyToDualHeader => "오른쪽 창으로 복사" + DualFolderSuffix;
    public string MoveToDualHeader => "오른쪽 창으로 이동" + DualFolderSuffix;

    // ── 폴더 비교 ──

    public bool IsCompareOn
    {
        get => Compare.IsOn;
        set => SetCompare(value);
    }

    public void SetCompare(bool on)
    {
        if (on && !_isDualVisible)
            SetDualPane(true);
        if (Compare.IsOn == on)
            return;
        Compare.IsOn = on;
        OnPropertyChanged(nameof(IsCompareOn));
        if (!on)
            Compare.Clear(FileList.AllItems.Concat(Dual.List.AllItems));
        RecomputeCompare();
    }

    void RecomputeCompare() =>
        Compare.Recompute(MainFolder, FileList.AllItems.ToList(), Dual.Folder, Dual.List.AllItems.ToList());

    /// <summary>
    /// Copies what one side has and the other lacks or has older. Older copies on the other side go to the
    /// Recycle Bin first, so one Ctrl+Z puts everything back exactly as it was.
    /// </summary>
    async Task SyncSideAsync(bool toRight)
    {
        if (MainFolder is not string left || !Dual.HasFolder)
            return;
        string target = toRight ? Dual.Folder : left;
        var source = FolderCompare.ToCopy(toRight ? FileList.AllItems : Dual.List.AllItems);
        if (source.Count == 0 || !_s.Dialogs.ConfirmOkCancel(CompareViewModel.CopyQuestion(toRight, source), "폴더 비교 — 복사"))
            return;
        var older = source.Where(i => i.CompareState == CompareState.Newer)
            .Select(i => Path.Combine(target, i.Name))
            .Where(DiskPaths.Exists)
            .ToList();
        var steps = new List<UndoAction>();
        if (older.Count > 0)
        {
            var started = DateTime.Now;
            await _s.FileOps.DeleteAsync(older, permanently: false, noConfirm: true, record: false);
            var gone = older.Where(p => !DiskPaths.Exists(p)).ToList();
            if (gone.Count > 0)
                steps.Add(new DeleteUndo(gone, started, DateTime.Now));
        }
        await _s.FileOps.TransferAsync(source.Select(i => i.FullPath).ToList(), target, move: false, record: false);
        var created = source.Select(i => Path.Combine(target, i.Name)).Where(DiskPaths.Exists).ToList();
        if (created.Count > 0)
            steps.Add(new CreateUndo(created, "비교 복사"));
        if (steps.Count > 0)
            _s.Undo.Push(new GroupUndo(steps, $"폴더 비교 복사 ({created.Count:N0}개)"));
        await Dual.ReloadAsync();
        await LoadFilesAsync(_active.FullPath);
        Status.Note($"{created.Count:N0}개를 {(toRight ? "오른쪽" : "왼쪽")}으로 복사했습니다 (Ctrl+Z로 되돌리기)", FileList);
    }
}
