using System.IO;
using FileStar.Platform;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>
/// #137: building the tree at start-up must not wait on the disk. A disconnected network drive or a cloud drive
/// that is still starting can take many seconds to answer; favorites and drives are probed in the background.
/// </summary>
public sealed class TreeStartupTests : IDisposable
{
    readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    /// <summary>A file system whose subfolder check waits until it is let go, as an unresponsive drive does.</summary>
    sealed class SlowFileSystem(IFileSystem inner) : IFileSystem
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly List<int> CheckedOnThreads = new();

        public bool ShowHidden { get => inner.ShowHidden; set => inner.ShowHidden = value; }
        public FileAttributes AttributesToSkip => inner.AttributesToSkip;
        public List<FileSystemInfo> GetEntries(string path) => inner.GetEntries(path);
        public List<DirectoryInfo> GetSubfolders(string path) => inner.GetSubfolders(path);
        public List<DirectoryInfo>? TryGetSubfolders(string path) => inner.TryGetSubfolders(path);

        public bool HasSubfolders(string path)
        {
            lock (CheckedOnThreads)
                CheckedOnThreads.Add(Environment.CurrentManagedThreadId);
            Gate.Wait(TimeSpan.FromSeconds(10));   // a test that wrongly waits here on the UI thread fails rather than hangs
            return inner.HasSubfolders(path);
        }
    }

    FolderTreeViewModel NewTree(IFileSystem files) =>
        new(new FolderTreeContext(files, _t.Notes), _t.Favorites, _t.Notes);

    /// <summary>The probes ask the real drives too; a disconnected one shouldn't hang the test run.</summary>
    static Task Probed(FolderTreeViewModel tree) => tree.Probing.WaitAsync(TimeSpan.FromSeconds(60));

    static FolderNode Favorite(FolderTreeViewModel tree, string path) =>
        tree.FavoritesNode!.Children.Single(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_slow_favorite_does_not_hold_up_building_the_tree() => WpfThread.Run(async () =>
    {
        string withSub = _t.Files.Folder(@"WithSub\inner");
        string parent = Path.GetDirectoryName(withSub)!;
        string flat = _t.Files.Folder("Flat");
        _t.Favorites.Add(parent);
        _t.Favorites.Add(flat);
        var files = new SlowFileSystem(_t.FileSystem);
        var tree = NewTree(files);
        int uiThread = Environment.CurrentManagedThreadId;

        tree.Build();

        // Built while the disk hasn't answered: both favorites are there, with an expander for now.
        Assert.Single(Favorite(tree, parent).Children);
        Assert.Single(Favorite(tree, flat).Children);
        Assert.False(tree.Probing.IsCompleted);

        files.Gate.Set();
        await Probed(tree);

        Assert.Single(Favorite(tree, parent).Children);   // still expandable: it has a subfolder
        Assert.Empty(Favorite(tree, flat).Children);      // the expander went once the probe found nothing
        lock (files.CheckedOnThreads)
        {
            Assert.NotEmpty(files.CheckedOnThreads);
            Assert.DoesNotContain(uiThread, files.CheckedOnThreads);
        }
    });

    [Fact]
    public void Drives_show_their_type_first_and_their_volume_label_once_probed() => WpfThread.Run(async () =>
    {
        var tree = NewTree(_t.FileSystem);
        var drive = DriveInfo.GetDrives().First(d => d.DriveType == DriveType.Fixed);

        tree.Build();
        var node = tree.Roots.SelectMany(r => r.Children)
            .Single(n => string.Equals(n.FullPath, drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Locations.DriveLabelByType(drive), node.Name);

        await Probed(tree);

        Assert.Equal(Locations.DriveLabel(drive), node.Name);
        Assert.Single(node.Children);   // a ready drive stays expandable
    });

    [Fact]
    public void A_favorite_added_later_is_probed_too() => WpfThread.Run(async () =>
    {
        string flat = _t.Files.Folder("Flat");
        var tree = NewTree(_t.FileSystem);
        tree.Build();
        await Probed(tree);

        _t.Favorites.Add(flat);
        tree.RebuildFavorites(flat);
        await Probed(tree);

        Assert.Empty(Favorite(tree, flat).Children);
    });

    [Fact]
    public void The_favorites_menu_gets_each_folders_own_icon_once_probed() => WpfThread.Run(async () =>
    {
        string folder = _t.Files.Folder("Fav");
        _t.Favorites.Add(folder);
        var vm = new MainViewModel(_t.Services, new[] { folder });
        vm.Start();

        // Built before the probe: the plain folder icon, without asking the shell about the folder.
        var plain = ShellInfo.GetIcon("", true);
        Assert.Same(plain, vm.FavoritesMenu.Single(m => m.ToolTip == folder).Icon);

        await Probed(vm.Tree);

        Assert.True(ShellInfo.TryGetCachedIcon(folder, true, exact: true, out var icon));
        var entry = vm.FavoritesMenu.Single(m => m.ToolTip == folder);
        Assert.Same(icon, entry.Icon);
    });
}
