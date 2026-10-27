namespace FileStar.Services;

/// <summary>Message boxes and simple prompts, owned by the main window. ViewModels ask through this instead of showing UI.</summary>
public interface IDialogService
{
    void Info(string message, string? title = null);

    void Warn(string message, string? title = null);

    /// <summary>예/아니요; true for 예.</summary>
    bool Confirm(string message, string title);

    /// <summary>확인/취소; true for 확인.</summary>
    bool ConfirmOkCancel(string message, string title);

    /// <summary>A one-line text prompt; the trimmed text, or null if cancelled or left empty.</summary>
    string? Ask(string title, string prompt, string initial = "");

    /// <summary>A folder picker; the chosen folder or null.</summary>
    string? PickFolder(string title, string? initial);
}

/// <summary>FileStar's own windows, opened on behalf of a ViewModel.</summary>
public interface IWindowService
{
    void ShowFavoritesManager(string currentPath, Favorite? select = null);

    /// <summary>True if the user saved new settings.</summary>
    bool ShowAiSettings(AiSettings settings);

    /// <summary>Modeless, so browsing can continue while a long batch runs.</summary>
    void ShowAiSummary(IReadOnlyList<string> files, Action onSaved);

    void ShowNotesRepair(Action onChanged);

    /// <summary>"이름 한꺼번에 바꾸기": (old full path, new name) for every item to rename, or null if cancelled.</summary>
    IReadOnlyList<(string Path, string NewName)>? AskBatchRename(IReadOnlyList<FileItem> items);

    /// <summary>
    /// Folders picked for AI turn into the readable files inside them (asking about subfolders and existing memos);
    /// files picked directly are kept. Null if cancelled.
    /// </summary>
    Task<List<string>?> ChooseAiFilesAsync(IReadOnlyList<FileItem> selected);
}
