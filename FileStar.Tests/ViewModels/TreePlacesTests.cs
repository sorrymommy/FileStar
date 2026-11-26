using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>
/// #155: the tree shows 바탕 화면 · 다운로드 · 문서 at the top, 네트워크 when there is a network and Linux when WSL
/// has distributions. Network and WSL folders are read off the UI thread; nothing here touches a real network.
/// </summary>
public sealed class TreePlacesTests : IDisposable
{
    readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    /// <summary>Answers for "\\NAS…" paths from a script (no network), off the UI thread only; local paths as usual.</summary>
    sealed class FakeNetworkFileSystem(IFileSystem local) : IFileSystem
    {
        public readonly Dictionary<string, string[]> Subfolders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<int> NetworkThreads = new();
        public int NetworkHasSubfoldersCalls;

        public bool ShowHidden { get => local.ShowHidden; set => local.ShowHidden = value; }
        public FileAttributes AttributesToSkip => local.AttributesToSkip;
        public List<FileSystemInfo> GetEntries(string path) => local.GetEntries(path);
        public List<DirectoryInfo> GetSubfolders(string path) => TryGetSubfolders(path) ?? new();

        public List<DirectoryInfo>? TryGetSubfolders(string path)
        {
            if (!Locations.IsUnc(path))
                return local.TryGetSubfolders(path);
            lock (NetworkThreads)
                NetworkThreads.Add(Environment.CurrentManagedThreadId);
            return Subfolders.TryGetValue(path, out var names) ? names.Select(n => new DirectoryInfo(Path.Join(path, n))).ToList() : null;
        }

        public bool HasSubfolders(string path)
        {
            if (Locations.IsUnc(path))
                Interlocked.Increment(ref NetworkHasSubfoldersCalls);
            return local.HasSubfolders(path);
        }
    }

    FolderTreeViewModel NewTree(IFileSystem files) =>
        new(new FolderTreeContext(files, _t.Notes), _t.Favorites, _t.Notes, _t.Places);

    static FolderNode Root(FolderTreeViewModel tree, string name) => tree.Roots.Single(r => r.Name == name);

    [Fact]
    public void Pinned_folders_sit_between_favorites_and_this_pc_and_keep_the_tree_in_their_branch() => WpfThread.Run(async () =>
    {
        string downloads = _t.Files.Folder("Downloads");
        string inner = _t.Files.Folder(@"Downloads\setup\x64");
        _t.Places.Pinned.Add(("다운로드", downloads));
        var vm = new MainViewModel(_t.Services, new[] { _t.Files.Path })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Wait.Until(() => vm.FileList.View != null, "the list to load");

        Assert.Equal(new[] { "즐겨찾기", "다운로드", Locations.ThisPc, "태그" }, vm.Tree.Roots.Select(r => r.Name));
        var pinned = Root(vm.Tree, "다운로드");
        Assert.True(pinned.IsRoot);
        Assert.False(pinned.IsMovable);   // the user's own Downloads folder: no cut, delete or rename from the tree
        await vm.TreeDeleteAsync(permanently: false, pinned);
        Assert.True(Directory.Exists(downloads));

        vm.Tree.OnSelected(pinned);
        vm.Navigate(inner);
        await Wait.Until(() => vm.Tree.Selected?.FullPath == inner, "the tree to follow");

        // Selected under 다운로드 itself, not under 내 PC › … › Downloads.
        Assert.Same(pinned, vm.Tree.Selected!.SelfAndAncestors().Last());
        Assert.Same(pinned, vm.Tree.Anchor);
    });

    [Fact]
    public void Network_shows_only_with_a_network_and_finds_its_computers_each_time_it_opens() => WpfThread.Run(async () =>
    {
        var files = new FakeNetworkFileSystem(_t.FileSystem);
        var offline = NewTree(files);
        offline.Build();
        Assert.DoesNotContain(offline.Roots, r => r.Name == "네트워크");

        _t.Places.IsNetworkAvailable = true;
        _t.Places.Computers.AddRange(new[] { @"\\NAS", @"\\nas" });   // found twice (e.g. by two discovery protocols)
        var tree = NewTree(files);
        tree.Build();
        var network = Root(tree, "네트워크");
        Assert.False(network.IsExpanded);
        Assert.Equal(0, _t.Places.Discoveries);   // not at start-up: discovery takes seconds
        Assert.Equal("불러오는 중...", Assert.Single(network.Children).Name);

        network.IsExpanded = true;
        await Wait.Until(() => network.Children.Any(c => c.FullPath != null), "the computers");
        var nas = Assert.Single(network.Children);
        Assert.Equal(("NAS", @"\\NAS"), (nas.Name, nas.FullPath));
        Assert.Equal(0, files.NetworkHasSubfoldersCalls);   // a computer gets an expander without being asked

        lock (_t.Places.Computers)
            _t.Places.Computers.Add(@"\\PRINTSRV");
        network.IsExpanded = false;
        network.IsExpanded = true;
        await Wait.Until(() => network.Children.Count == 2, "the new computer");
        Assert.Same(nas, network.Children[0]);   // kept, not rebuilt
        Assert.Equal(2, _t.Places.Discoveries);
    });

    [Fact]
    public void Finding_no_computers_says_so_and_opening_again_looks_again() => WpfThread.Run(async () =>
    {
        _t.Places.IsNetworkAvailable = true;
        var tree = NewTree(new FakeNetworkFileSystem(_t.FileSystem));
        tree.Build();
        var network = Root(tree, "네트워크");

        network.IsExpanded = true;
        await Wait.Until(() => _t.Places.Discoveries == 1 && network.Children.FirstOrDefault()?.Name == "찾은 컴퓨터 없음", "the empty result");
        Assert.Null(Assert.Single(network.Children).FullPath);

        lock (_t.Places.Computers)
            _t.Places.Computers.Add(@"\\NAS");
        network.IsExpanded = false;
        network.IsExpanded = true;
        await Wait.Until(() => network.Children.FirstOrDefault()?.FullPath == @"\\NAS", "the computer");
        Assert.Single(network.Children);
    });

    [Fact]
    public void A_network_path_is_followed_as_its_folders_are_read_in_the_background() => WpfThread.Run(async () =>
    {
        var files = new FakeNetworkFileSystem(_t.FileSystem);
        files.Subfolders[@"\\NAS"] = new[] { "share" };
        files.Subfolders[@"\\NAS\share"] = new[] { "docs", "photos" };
        files.Subfolders[@"\\NAS\share\docs"] = Array.Empty<string>();
        _t.Places.IsNetworkAvailable = true;
        _t.Places.Computers.Add(@"\\NAS");
        var tree = NewTree(files);
        tree.Build();
        int uiThread = Environment.CurrentManagedThreadId;

        tree.Sync(@"\\NAS\share\docs");

        await Wait.Until(() => tree.Selected?.FullPath == @"\\NAS\share\docs", "the tree to reach the folder");
        Assert.Equal(new[] { "네트워크", "NAS", "share", "docs" }, tree.PathChain.Select(n => n.Name));
        Assert.All(tree.PathChain, n => Assert.True(n.IsOnPath));
        lock (files.NetworkThreads)
            Assert.DoesNotContain(uiThread, files.NetworkThreads);
        Assert.Equal(0, files.NetworkHasSubfoldersCalls);
    });

    [Fact]
    public void A_network_folder_that_cannot_be_read_loses_its_expander() => WpfThread.Run(async () =>
    {
        var files = new FakeNetworkFileSystem(_t.FileSystem);   // knows nothing: every network read fails
        _t.Places.IsNetworkAvailable = true;
        _t.Places.Computers.Add(@"\\GONE");
        var tree = NewTree(files);
        tree.Build();
        var network = Root(tree, "네트워크");
        network.IsExpanded = true;
        await Wait.Until(() => network.Children.Any(c => c.FullPath != null), "the computers");

        var gone = network.Children[0];
        gone.IsExpanded = true;
        await Wait.Until(() => gone.Children.Count == 0, "the expander to go");
    });

    [Fact]
    public void Linux_lists_the_distributions_without_starting_wsl() => WpfThread.Run(() =>
    {
        var files = new FakeNetworkFileSystem(_t.FileSystem);
        var withoutWsl = NewTree(files);
        withoutWsl.Build();
        Assert.DoesNotContain(withoutWsl.Roots, r => r.Name == "Linux");

        _t.Places.Distros.AddRange(new[] { @"\\wsl.localhost\Ubuntu", @"\\wsl.localhost\docker-desktop" });
        var tree = NewTree(files);
        tree.Build();

        var linux = Root(tree, "Linux");
        Assert.False(linux.IsExpanded);
        Assert.Equal(new[] { "Ubuntu", "docker-desktop" }, linux.Children.Select(c => c.Name));
        Assert.Equal(new[] { "즐겨찾기", Locations.ThisPc, "Linux", "태그" }, tree.Roots.Select(r => r.Name));
        Assert.Equal(0, files.NetworkHasSubfoldersCalls);
        lock (files.NetworkThreads)
            Assert.Empty(files.NetworkThreads);
        return Task.CompletedTask;
    });
}
