using System.Windows;
using FileStar.Views;
using Velopack;

namespace FileStar;

public partial class App : Application
{
    readonly string[] _args;

    App(string[] args) => _args = args;

    [STAThread]
    static void Main(string[] args)
    {
        // First, before any WPF start-up: Velopack's install/update/uninstall steps run here and exit.
        VelopackApp.Build().Run();
        var app = new App(args);
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>The composition root: every service is created here once and handed to whoever needs it.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Perf.Log("--- start");
        Perf.TraceBindings();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);   // CP949: Korean text files and zips

        var owner = new WindowOwner();
        var dialogs = new DialogService(owner);
        // A failed file operation (access denied, file in use, a drive pulled out) must not close the window.
        DispatcherUnhandledException += (_, args) =>
        {
            Perf.Log("unhandled: " + args.Exception);
            args.Handled = true;
            dialogs.Warn("작업을 완료하지 못했습니다.\n\n" + args.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Perf.Log("unobserved: " + args.Exception);
            args.SetObserved();
        };
        var notes = SqliteNotesStore.Open(AppPaths.DataFile("notes.db"));
        var undo = new UndoHistory();
        var recent = new RecentItems();
        var fileSystem = new LocalFileSystem();
        var aiSettings = new JsonAiSettingsStore(AppPaths.DataFile("ai.json"));
        var favorites = JsonFavoritesStore.Load(AppPaths.DataFile("favorites.json"));
        var services = new AppServices(
            Notes: notes,
            Favorites: favorites,
            Settings: new JsonSettingsStore(AppPaths.DataFile("settings.json")),
            Recent: recent,
            Undo: undo,
            FileOps: new ShellFileOperations(notes, undo, owner),
            FileSystem: fileSystem,
            Watch: new FolderWatchService(),
            Dialogs: dialogs,
            Windows: new WindowService(owner, dialogs, notes, favorites, fileSystem, aiSettings),
            Shell: new ShellService(recent),
            Theme: new ThemeManager(),
            Updates: new VelopackUpdateService(),
            AiSettings: aiSettings);

        var window = new MainWindow(new MainViewModel(services, _args));
        owner.Window = window;
        MainWindow = window;
        window.Show();
    }
}
