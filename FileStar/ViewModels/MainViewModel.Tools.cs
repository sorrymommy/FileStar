using System.IO;

namespace FileStar.ViewModels;

// Search tabs, undo, AI memos, memo repair, recent items, updates and help.
public sealed partial class MainViewModel
{
    bool _repairRunning, _checkingForUpdates;
    DateTime _lastRepair;

    // ── 검색 탭 ──

    /// <summary>Focuses this tab's search panel, or opens a search tab for the folder being looked at.</summary>
    public void OpenSearch()
    {
        if (!IsDualActive && CurrentSearch is { Kind: not SearchKind.Tag })
        {
            Focus(FocusTarget.SearchBox);
            return;
        }
        string? folder = IsDualActive ? Dual.Folder
            : CurrentSearch is { Kind: SearchKind.Here } here ? here.Base
            : InVirtual ? null
            : _active.FullPath;
        if (folder != null && Locations.IsVirtual(folder))
            folder = null;
        NewTab(SearchLocation.Find(folder, "", Search.LastFields, Search.LastSubfolders).Path);
        Focus(FocusTarget.SearchBox);
    }

    /// <summary>Runs the search the panel describes, in this tab (the previous one stays in 뒤로).</summary>
    void RunSearch(SearchLocation search)
    {
        RequestSave();
        if (search.Path == _active.FullPath)
            Refresh();
        else
            Navigate(search.Path);
    }

    // ── Undo (Ctrl+Z) ──

    public string UndoHeader => _s.Undo.Peek is { } next ? "실행 취소: " + next.Description : "실행 취소";

    public bool CanUndo => _s.Undo.Peek != null;

    public async Task UndoLastAsync()
    {
        if (_s.Undo.Peek == null)
        {
            Status.Show("되돌릴 작업이 없습니다.");
            return;
        }
        Notes.ResolvePendingEdit();
        var (action, problem) = await _s.Undo.UndoAsync(_undoContext);
        if (action == null)
            return;
        // The folder being shown may itself have been renamed back or removed: step out to what still exists.
        if (!InVirtual && !Directory.Exists(_active.FullPath))
        {
            string? folder = _active.FullPath;
            while (folder != null && !Directory.Exists(folder))
                folder = Path.GetDirectoryName(folder);
            if (folder != null)
                Navigate(folder);
        }
        // Folder names may have changed beside the current one; reloading must not move the view.
        Tree.ReloadAround(parentToo: true);
        Tree.Sync(_active.FullPath);
        Status.ShowDrive(_active.FullPath);
        await LoadFilesAsync(_active.FullPath);
        Status.Note("되돌림: " + action.Description + (problem != null ? " · " + problem : ""), FileList);
    }

    // ── AI memo/tags ──

    public async Task RunAiSummaryAsync()
    {
        var selected = InfoSelection;
        if (selected.Count == 0 || InThisPc && !IsDualActive)
        {
            _s.Dialogs.Info("AI로 요약할 파일이나 폴더를 목록에서 선택하세요. (여러 개를 선택하면 차례로 처리하고, 폴더를 고르면 그 안의 파일을 찾아 목록에 올립니다.)",
                "AI로 메모·태그 만들기");
            return;
        }
        // Folders turn into the readable files inside them (the dialog asks about subfolders and existing memos).
        var files = await _s.Windows.ChooseAiFilesAsync(selected);
        if (files is not { Count: > 0 })
            return;
        Notes.ResolvePendingEdit();
        Notes.CommitTags();
        _s.Windows.ShowAiSummary(files, ReloadNotes);
    }

    void ShowAiSettings() => _s.Windows.ShowAiSettings(_s.AiSettings.Load());

    // ── Memos that lost their file (moved by another program) ──

    /// <summary>The window came back to the front: other windows may have changed memos, other programs may have moved files.</summary>
    public async Task OnActivatedAsync()
    {
        if (_s.Notes.ReloadIfChangedElsewhere())
            RefreshNoteColumns();
        await AutoRepairNotesAsync();
    }

    /// <summary>
    /// Quietly reattaches memos to files that another program moved (same name, found by file ID). Runs at
    /// start-up and whenever FileStar is switched back to, at most once every 30 seconds.
    /// </summary>
    async Task AutoRepairNotesAsync()
    {
        if (_repairRunning || DateTime.Now - _lastRepair < TimeSpan.FromSeconds(30))
            return;
        _repairRunning = true;
        _lastRepair = DateTime.Now;
        try
        {
            var snapshot = _s.Notes.Snapshot();
            var result = await Task.Run(() => NotesRepair.Scan(snapshot));
            int relinked = NotesRepair.AutoRelink(_s.Notes, result);
            if (relinked > 0)
            {
                RefreshNoteColumns();
                Status.Show($"다른 곳으로 옮겨진 파일 {relinked:N0}개를 찾아 메모를 다시 연결했습니다.");
            }
        }
        catch { }
        finally
        {
            _repairRunning = false;
        }
    }

    void ShowNotesRepair() => _s.Windows.ShowNotesRepair(RefreshNoteColumns);

    // ── 최근 폴더 / 최근 파일 ──

    public IReadOnlyList<MenuItemViewModel> RecentMenu(bool folders)
    {
        var paths = (folders ? _s.Recent.Folders : _s.Recent.Files)
            .Where(p => folders ? Directory.Exists(p) : File.Exists(p))   // skip what was moved or deleted since
            .ToList();
        var menu = paths.Select(path => new MenuItemViewModel
        {
            Header = folders ? Locations.DisplayName(path) : Path.GetFileName(path),
            Gesture = folders ? Path.GetDirectoryName(path) ?? "" : Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
            ToolTip = path,
            Icon = ShellInfo.GetIcon(path, folders),
            Command = new RelayCommand(() =>
            {
                if (folders)
                    Go(path);
                else
                    ShellOpen(path);
            }),
        }).ToList();
        if (paths.Count == 0)
            menu.Add(MenuItemViewModel.Note("(없음)"));
        menu.Add(MenuItemViewModel.Separator);
        menu.Add(MenuItemViewModel.Action("최근 목록 지우기", () =>
        {
            _s.Recent.Clear();
            RequestSave();
        }, paths.Count > 0));
        return menu;
    }

    // ── Windows ──

    /// <summary>Ctrl+N: another FileStar window on the current folder.</summary>
    public void OpenNewWindow()
    {
        try
        {
            _s.Shell.StartNewWindow(_active.FullPath);
        }
        catch (Exception ex)
        {
            _s.Dialogs.Warn("새 창을 열 수 없습니다: " + ex.Message);
        }
    }

    // ── Updates (도움말 > 업데이트 확인, and a quiet check a few seconds after start-up) ──

    /// <summary>Called once the window is on screen.</summary>
    public async Task OnLoadedAsync()
    {
        if (_s.Notes.StorageError is string storageError)
            _s.Dialogs.Warn(storageError + "\n\n이번 실행 동안 바꾼 메모는 저장되지 않습니다.");
        await CheckForUpdatesAsync(quiet: true);
    }

    /// <param name="quiet">The start-up check: says nothing unless an update is ready.</param>
    public async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_checkingForUpdates)
            return;
        var updates = _s.Updates;
        if (!updates.IsInstalled)
        {
            if (!quiet)
                _s.Dialogs.Info("이 FileStar는 설치 프로그램(FileStar-win-Setup.exe)으로 설치한 것이 아니라서 자동 업데이트를 할 수 없습니다.\n\n" +
                                $"새 버전은 {updates.RepoUrl}/releases 에서 받을 수 있습니다.");
            return;
        }
        _checkingForUpdates = true;
        try
        {
            if (quiet)
                await Task.Delay(TimeSpan.FromSeconds(5));
            else
                Status.Note("업데이트 확인 중...", FileList);
            string? version = await updates.CheckAndDownloadAsync(percent =>
                Defer(() => Status.Note($"새 버전 받는 중... {percent}%", FileList)));
            if (version == null)
            {
                if (!quiet)
                {
                    Status.Note("최신 버전입니다.", FileList);
                    _s.Dialogs.Info($"최신 버전을 사용하고 있습니다. (FileStar {updates.CurrentVersion})");
                }
                return;
            }
            Status.Note($"FileStar {version} 업데이트 준비됨 — 닫을 때 설치됩니다.", FileList);
            if (_s.Dialogs.Confirm($"새 버전 FileStar {version}을(를) 받았습니다. (지금 {updates.CurrentVersion})\n\n" +
                                   "지금 다시 시작해서 설치할까요?\n'아니요'를 누르면 FileStar를 닫을 때 설치됩니다.", AppName))
            {
                // Save first: the update closes FileStar.
                SaveBeforeExit();
                updates.ApplyAndRestart();
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
                _s.Dialogs.Warn("업데이트를 확인할 수 없습니다: " + ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    void ShowAbout() => _s.Dialogs.Info($"FileStar {_s.Updates.CurrentVersion}\n\n{_s.Updates.RepoUrl}\n\n" +
                                        (_s.Updates.IsInstalled ? "자동 업데이트: 켜짐 (시작할 때 새 버전 확인)" : "자동 업데이트: 꺼짐 (설치 프로그램으로 설치하지 않음)"),
        "FileStar 정보");

    void ShowShortcutHelp() => _s.Dialogs.Info("""
        [이동]
        Alt+← / Backspace   뒤로              Alt+→   앞으로
        Alt+↑               상위 폴더        Ctrl+Shift+P   내 PC
        Ctrl+L / Alt+D / F4  주소창          F6 / Shift+F6   영역 전환
        Ctrl+F / Ctrl+E / F3  검색 탭 (이름·확장자·태그·메모에서 찾기)
        F5 / Ctrl+R         새로 고침         Ctrl+Z        실행 취소 (이름·이동·복사·삭제·새 폴더)
        F11                 전체 화면

        [파일]
        Enter               열기             Ctrl+Enter   새 탭에서 열기
        F2                  이름 바꾸기       Ctrl+Shift+N  새 폴더
        Del / Ctrl+D        휴지통으로 삭제   Shift+Del     영구 삭제
        Ctrl+C / X / V      복사 / 잘라내기 / 붙여넣기
        Ctrl+Shift+C        경로 복사         Ctrl+A        모두 선택
        Alt+Enter           속성             Shift+F10     오른쪽 클릭 메뉴
        글자 입력            그 글자로 시작하는 파일로 이동
        이름 바꾸는 중 ↑/↓ (Tab)   저장하고 이전/다음 파일 이름 바꾸기
        왼쪽 트리에서도      Ctrl+C / X / V, Del, F2 (선택한 폴더에 적용)

        [창 · 탭]
        Ctrl+N  새 창    Ctrl+T  새 탭    Ctrl+W  탭 닫기    Ctrl+Tab  다음 탭

        [창 영역]
        Alt+P  미리보기    Alt+Shift+P  속성    Ctrl+Shift+E  트리를 현재 폴더까지 펼치기
        Ctrl+Shift+D  두 창 보기 — 주소창·뒤로/앞으로·트리·필터·정보 창은 파란 줄이 있는(활성) 창을 따릅니다
        Ctrl+Shift+K  폴더 비교 (이름·크기·수정 날짜로 비교, 다른 것만 보기, 한쪽으로 복사)

        [FileStar 기능]
        Ctrl+B  즐겨찾기 추가/제거    Ctrl+1~9  즐겨찾기 열기
        Ctrl+M  메모 수정 (Ctrl+S 저장 · Esc 취소)    Ctrl+Shift+M  AI로 메모·태그    F9  정보 창
        Ctrl+H  숨김 파일 표시    Ctrl+Shift+H  메모·태그 표시 켜기/끄기
        Shift+오른쪽 클릭  탐색기 메뉴 바로 열기
        """, AppName + " 단축키 (F1)");
}
