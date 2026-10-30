using System.Windows.Input;

namespace FileStar.ViewModels;

// The commands the menus, toolbar and context menus bind to. Keyboard shortcuts that depend on where the
// focus is are routed by the view to the same methods.
public sealed partial class MainViewModel
{
    /// <summary>Whether Shift is held right now (Shift+Delete deletes permanently); set by the view.</summary>
    public Func<bool> IsShiftDown { get; set; } = () => false;

    // ── Navigation, tabs, windows ──
    public ICommand GoBackCommand { get; private set; } = null!;
    public ICommand GoForwardCommand { get; private set; } = null!;
    public ICommand GoUpCommand { get; private set; } = null!;
    public ICommand GoSpecialCommand { get; private set; } = null!;
    public ICommand CrumbCommand { get; private set; } = null!;
    public ICommand RefreshCommand { get; private set; } = null!;
    public ICommand NewTabCommand { get; private set; } = null!;
    public ICommand CloseActiveTabCommand { get; private set; } = null!;
    public ICommand ActivateTabCommand { get; private set; } = null!;
    public ICommand CloseTabCommand { get; private set; } = null!;
    public ICommand PinTabCommand { get; private set; } = null!;
    public ICommand NewWindowCommand { get; private set; } = null!;
    public ICommand OpenCurrentInExplorerCommand { get; private set; } = null!;
    public ICommand OpenSearchCommand { get; private set; } = null!;

    // ── View toggles (toolbar buttons; the menus bind the properties) ──
    public ICommand ToggleHiddenCommand { get; private set; } = null!;
    public ICommand ToggleNoteMarksCommand { get; private set; } = null!;
    public ICommand ToggleDarkModeCommand { get; private set; } = null!;

    // ── Files (the list's context menu and the toolbar) ──
    public ICommand NewFolderCommand { get; private set; } = null!;
    public ICommand OpenCommand { get; private set; } = null!;
    public ICommand OpenInNewTabCommand { get; private set; } = null!;
    public ICommand OpenLocationCommand { get; private set; } = null!;
    public ICommand ShowInExplorerCommand { get; private set; } = null!;
    public ICommand CutCommand { get; private set; } = null!;
    public ICommand CopyCommand { get; private set; } = null!;
    public ICommand PasteCommand { get; private set; } = null!;
    public ICommand CopyToDualCommand { get; private set; } = null!;
    public ICommand MoveToDualCommand { get; private set; } = null!;
    public ICommand DeleteCommand { get; private set; } = null!;
    public ICommand RenameCommand { get; private set; } = null!;
    public ICommand BatchRenameCommand { get; private set; } = null!;
    public ICommand ZipCommand { get; private set; } = null!;
    public ICommand UnzipCommand { get; private set; } = null!;
    public ICommand CopyPathCommand { get; private set; } = null!;
    public ICommand PropertiesCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;

    // ── 폴더 비교 ──
    public ICommand CompareOffCommand { get; private set; } = null!;
    public ICommand CopyRightCommand { get; private set; } = null!;
    public ICommand CopyLeftCommand { get; private set; } = null!;

    // ── Memos, AI, tools, help ──
    public ICommand EditNoteCommand { get; private set; } = null!;
    public ICommand AiSummaryCommand { get; private set; } = null!;
    public ICommand AiSettingsCommand { get; private set; } = null!;
    public ICommand NotesRepairCommand { get; private set; } = null!;
    public ICommand ShortcutHelpCommand { get; private set; } = null!;
    public ICommand CheckForUpdatesCommand { get; private set; } = null!;
    public ICommand AboutCommand { get; private set; } = null!;

    void CreateCommands()
    {
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        GoForwardCommand = new RelayCommand(GoForward, () => CanGoForward);
        GoUpCommand = new RelayCommand(GoUp, () => CanGoUp);
        GoSpecialCommand = new RelayCommand<string>(key =>
        {
            if (ThisPcItems.SpecialFolder(key) is string path)
                Go(path);
        });
        CrumbCommand = new RelayCommand<string>(path => Go(path));
        RefreshCommand = new RelayCommand(RefreshActive);
        NewTabCommand = new RelayCommand(() => NewTab(_active.FullPath));
        CloseActiveTabCommand = new RelayCommand(() => CloseTab(_active));
        ActivateTabCommand = new RelayCommand<BrowserTab>(tab =>
        {
            if (tab != _active)
                ActivateTab(tab);
        });
        CloseTabCommand = new RelayCommand<BrowserTab>(CloseTab);
        PinTabCommand = new RelayCommand<BrowserTab>(TogglePin);
        NewWindowCommand = new RelayCommand(OpenNewWindow);
        OpenCurrentInExplorerCommand = new RelayCommand(() => _s.Shell.OpenInExplorer(_active.FullPath, select: false));
        OpenSearchCommand = new RelayCommand(OpenSearch);

        ToggleHiddenCommand = new RelayCommand(() => ShowHidden = !ShowHidden);
        ToggleNoteMarksCommand = new RelayCommand(() => ShowNoteMarks = !ShowNoteMarks);
        ToggleDarkModeCommand = new RelayCommand(() => IsDarkMode = !IsDarkMode);

        NewFolderCommand = new RelayCommand(NewFolder, () => CanCreateFolder);
        OpenCommand = new RelayCommand(OpenSelected, () => AnySelected);
        OpenInNewTabCommand = new RelayCommand(OpenSelectedFoldersInNewTabs, () => FileList.Selected.Any(i => i.IsDirectory));
        OpenLocationCommand = new RelayCommand(OpenItemLocation, () => CanOpenLocation);
        ShowInExplorerCommand = new RelayCommand(ShowInExplorer);
        CutCommand = new RelayCommand(() => ClipboardSelected(cut: true), () => Editable && AnySelected);
        CopyCommand = new RelayCommand(() => ClipboardSelected(cut: false), () => Editable && AnySelected);
        PasteCommand = new AsyncRelayCommand(PasteIntoCurrentFolderAsync, () => !InVirtual && _s.FileOps.ClipboardHasFiles);
        CopyToDualCommand = new AsyncRelayCommand(() => SendToDualAsync(move: false), () => DualMenuVisible);
        MoveToDualCommand = new AsyncRelayCommand(() => SendToDualAsync(move: true), () => DualMenuVisible && Editable);
        DeleteCommand = new RelayCommand(() => _ = DeleteSelectedAsync(IsShiftDown()), () => Editable && AnySelected);
        RenameCommand = new RelayCommand(RenameSelected, () => Editable && FileList.Selected.Count == 1);
        BatchRenameCommand = new RelayCommand(BatchRename, () => Editable && FileList.Selected.Count >= 2);
        ZipCommand = new AsyncRelayCommand(ZipSelectedAsync, () => Editable && AnySelected);
        UnzipCommand = new AsyncRelayCommand(UnzipSelectedAsync, () => CanUnzip);
        CopyPathCommand = new RelayCommand(CopySelectedPaths);
        PropertiesCommand = new RelayCommand(ShowSelectedProperties, () => AnySelected);
        UndoCommand = new AsyncRelayCommand(UndoLastAsync, () => CanUndo);

        CompareOffCommand = new RelayCommand(() => SetCompare(false));
        CopyRightCommand = new AsyncRelayCommand(() => SyncSideAsync(toRight: true), () => Compare.CanCopyRight);
        CopyLeftCommand = new AsyncRelayCommand(() => SyncSideAsync(toRight: false), () => Compare.CanCopyLeft);

        EditNoteCommand = new RelayCommand(EditNote);
        AiSummaryCommand = new AsyncRelayCommand(RunAiSummaryAsync);
        AiSettingsCommand = new RelayCommand(ShowAiSettings);
        NotesRepairCommand = new RelayCommand(ShowNotesRepair);
        ShortcutHelpCommand = new RelayCommand(ShowShortcutHelp);
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(quiet: false));
        AboutCommand = new RelayCommand(ShowAbout);
    }

    // ── Sorting (both lists the same way, so they line up for comparing) ──

    public void SortBy(string key)
    {
        if (!FileItemComparer.SortableKeys.Contains(key))
            return;
        bool descending = key == FileList.SortKey
            ? !FileList.SortDescending
            : key is "Size" or "Modified" or "Created";   // sizes and dates are most useful largest/newest first
        FileList.SetSort(key, descending);
        Dual.List.SetSort(key, descending);
        SortChanged?.Invoke();
        RequestSave();
    }
}
