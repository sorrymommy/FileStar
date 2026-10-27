namespace FileStar.Services;

/// <summary>Every service the main window's ViewModel works through, created once in <c>App</c>.</summary>
public sealed record AppServices(
    INotesStore Notes,
    IFavoritesStore Favorites,
    ISettingsStore Settings,
    IRecentItems Recent,
    IUndoHistory Undo,
    IFileOperations FileOps,
    IFileSystem FileSystem,
    IFolderWatchService Watch,
    IDialogService Dialogs,
    IWindowService Windows,
    IShellService Shell,
    IThemeService Theme,
    IUpdateService Updates,
    IAiSettingsStore AiSettings);
