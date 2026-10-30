using System.Windows;
using Microsoft.Win32;

namespace FileStar.Views;

/// <summary>Message boxes and prompts over whichever FileStar window is active (a dialog, or the main window).</summary>
public sealed class DialogService(IWindowOwner owner) : IDialogService
{
    internal Window Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner.Window ?? Application.Current.MainWindow;

    public void Info(string message, string? title = null) =>
        MessageBox.Show(Owner, message, title ?? AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string? title = null) =>
        MessageBox.Show(Owner, message, title ?? AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ConfirmOkCancel(string message, string title) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public string? Ask(string title, string prompt, string initial = "") => InputDialog.Ask(Owner, title, prompt, initial);

    public string? PickFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
            dialog.InitialDirectory = initial;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }
}

/// <summary>Opens FileStar's own windows for the ViewModels, each with its ViewModel.</summary>
public sealed class WindowService(IWindowOwner owner, DialogService dialogs, INotesStore notes, IFavoritesStore favorites,
    IFileSystem fileSystem, IAiSettingsStore aiSettings) : IWindowService
{
    readonly IAiSummarizer _summarizer = AiSummarizer.CreateDefault();
    readonly IAiModelCatalog _catalog = new AiModelCatalog();
    readonly IDocumentReader _reader = new FileDocumentReader();

    public void ShowFavoritesManager(string currentPath, Favorite? select = null)
    {
        var vm = new FavoritesManagerViewModel(favorites, dialogs, currentPath, select);
        new FavoritesWindow(vm) { Owner = dialogs.Owner }.ShowDialog();
    }

    public bool ShowAiSettings(AiSettings settings)
    {
        var vm = new AiSettingsViewModel(settings, aiSettings, _catalog, dialogs);
        return new AiSettingsWindow(vm) { Owner = dialogs.Owner }.ShowDialog() == true;
    }

    public void ShowAiSummary(IReadOnlyList<string> files, Action onSaved)
    {
        var vm = new AiSummaryViewModel(files, aiSettings, _summarizer, _reader, notes, this, dialogs, onSaved);
        new AiSummaryWindow(vm) { Owner = owner.Window }.Show();
    }

    public void ShowNotesRepair(Action onChanged) =>
        new NotesRepairWindow(new NotesRepairViewModel(notes, onChanged)) { Owner = dialogs.Owner }.ShowDialog();

    public IReadOnlyList<(string Path, string NewName)>? AskBatchRename(IReadOnlyList<FileItem> items)
    {
        var vm = new BatchRenameViewModel(items);
        return new BatchRenameWindow(vm) { Owner = dialogs.Owner }.ShowDialog() == true ? vm.Plan() : null;
    }

    public async Task<List<string>?> ChooseAiFilesAsync(IReadOnlyList<FileItem> selected)
    {
        var files = selected.Where(i => !i.IsDirectory).Select(i => i.FullPath).ToList();
        var folders = selected.Where(i => i.IsDirectory).Select(i => i.FullPath).ToList();
        if (folders.Count == 0)
            return files;

        // A big folder tree takes a while to walk.
        var window = dialogs.Owner;
        window.Cursor = System.Windows.Input.Cursors.AppStarting;
        AiFolderScan scan;
        try
        {
            scan = await AiFileCollector.ScanAsync(folders, fileSystem.AttributesToSkip, _reader);
        }
        finally
        {
            window.Cursor = null;
        }
        if (scan.All.Count == 0)
        {
            dialogs.Info("선택한 폴더에 AI가 읽을 수 있는 파일(문서·한글·PDF·이미지·텍스트)이 없습니다.", "AI로 메모·태그 만들기");
            return files.Count > 0 ? files : null;
        }
        var vm = new AiFolderChoiceViewModel(scan, notes);
        if (new AiFolderChoiceWindow(vm) { Owner = window }.ShowDialog() != true)
            return null;
        return files.Concat(vm.Chosen).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
