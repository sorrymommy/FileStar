using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>
/// The folder tree follows folders made, deleted or renamed where a list is showing — by FileStar or by another
/// program — without its "하위 폴더 새로 고침" command.
/// </summary>
public sealed class TreeAutoRefreshTests : IDisposable
{
    readonly TestServices _t = new();

    public TreeAutoRefreshTests()
    {
        _t.Files.Folder(@"Work\B");
        _t.Files.Folder(@"Work\B\deep");
        _t.Files.Folder(@"Work\D");
        _t.Files.File(@"Work\notes.txt");
        _t.Files.File(@"Empty\only.txt");
    }

    public void Dispose() => _t.Dispose();

    async Task<MainViewModel> StartAsync(string folder)
    {
        var vm = new MainViewModel(_t.Services, new[] { folder })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Loaded(vm);
        vm.ShowHidden = true;   // the saved setting (off) was applied on start; this rebuilds the tree
        await Loaded(vm);
        return vm;
    }

    static Task Loaded(MainViewModel vm) =>
        Wait.Until(() => vm.FileList.LoadingMessage == null && vm.FileList.View != null, "the list to load");

    static FolderNode Node(MainViewModel vm, string path) =>
        vm.Tree.Roots.SelectMany(r => r.SelfAndDescendants())
            .First(n => n.IsFolder && string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));

    static List<string> ChildNames(FolderNode node) => node.Children.Select(c => c.Name).ToList();

    [Fact]
    public void A_new_folder_made_in_the_list_appears_in_the_tree() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(work);
        var node = Node(vm, work);
        node.IsExpanded = true;
        Assert.Equal(new[] { "B", "D" }, ChildNames(node));

        vm.NewFolder();
        await Wait.Until(() => node.Children.Any(c => c.Name == "새 폴더"), "the new folder in the tree");

        Assert.Equal(new[] { "B", "D", "새 폴더" }, ChildNames(node));
        Assert.Equal(work, vm.ActiveTab.FullPath);   // the tree didn't navigate anywhere
    });

    [Fact]
    public void Folders_made_and_deleted_by_another_program_follow_and_open_branches_stay_open() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(work);
        var node = Node(vm, work);
        node.IsExpanded = true;
        var b = node.Children.Single(c => c.Name == "B");
        b.IsExpanded = true;
        Assert.Equal(new[] { "deep" }, ChildNames(b));

        _t.Files.Folder(@"Work\C");
        Directory.Delete(_t.Files.Combine(@"Work\D"));
        _t.Watch.Changed(work);
        await Wait.Until(() => node.Children.Any(c => c.Name == "C"), "the tree to follow the folder");

        Assert.Equal(new[] { "B", "C" }, ChildNames(node));
        Assert.Same(b, node.Children[0]);   // kept, not rebuilt
        Assert.True(b.IsExpanded);
        Assert.Equal(new[] { "deep" }, ChildNames(b));
        Assert.All(node.Children, c => Assert.Same(node, c.Parent));
        Assert.True(node.Children[^1].IsLast);
        Assert.False(node.Children[0].IsLast);
    });

    [Fact]
    public void A_folder_without_subfolders_gets_its_first_one_and_loses_its_last() => WpfThread.Run(async () =>
    {
        string empty = _t.Files.Combine("Empty");
        var vm = await StartAsync(empty);
        var node = Node(vm, empty);
        Assert.Empty(node.Children);

        _t.Files.Folder(@"Empty\Fresh");
        _t.Watch.Changed(empty);
        await Wait.Until(() => node.Children.Count == 1, "the first subfolder");
        Assert.Equal("Fresh", node.Children[0].Name);
        Assert.Equal(_t.Files.Combine(@"Empty\Fresh"), node.Children[0].FullPath);

        Directory.Delete(_t.Files.Combine(@"Empty\Fresh"));
        _t.Watch.Changed(empty);
        await Wait.Until(() => node.Children.Count == 0, "the last subfolder to go");
    });

    [Fact]
    public void A_renamed_folder_shows_its_new_name_even_when_only_the_case_changed() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(work);
        var node = Node(vm, work);
        node.IsExpanded = true;

        Directory.Move(_t.Files.Combine(@"Work\D"), _t.Files.Combine(@"Work\d_tmp"));
        Directory.Move(_t.Files.Combine(@"Work\d_tmp"), _t.Files.Combine(@"Work\d"));
        _t.Watch.Changed(work);

        await Wait.Until(() => ChildNames(node).Contains("d"), "the renamed folder");
        Assert.Equal(new[] { "B", "d" }, ChildNames(node));
    });

    [Fact]
    public void A_folder_changed_in_the_second_list_updates_the_tree_too() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        string b = _t.Files.Combine(@"Work\B");
        var vm = await StartAsync(work);
        vm.SetDualPane(true, b);
        await Wait.Until(() => vm.Dual.Folder == b && vm.Dual.List.LoadingMessage == null && vm.Dual.List.View != null, "the second list");
        var workNode = Node(vm, work);
        workNode.IsExpanded = true;
        var bNode = workNode.Children.Single(c => c.Name == "B");
        bNode.IsExpanded = true;

        _t.Files.Folder(@"Work\B\second");
        _t.Watch.Changed(b);

        await Wait.Until(() => ChildNames(bNode).Contains("second"), "the second list's new folder in the tree");
        Assert.Equal(new[] { "deep", "second" }, ChildNames(bNode));
    });

    [Fact]
    public void Opening_a_branch_again_shows_folders_made_while_it_was_closed() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(_t.Files.Combine("Empty"));
        var node = Node(vm, work);
        node.IsExpanded = true;
        node.IsExpanded = false;

        _t.Files.Folder(@"Work\Later");   // not shown in any list
        node.IsExpanded = true;

        Assert.Equal(new[] { "B", "D", "Later" }, ChildNames(node));
    });

    [Fact]
    public void The_path_line_is_redrawn_when_a_folder_appears_above_the_location() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(_t.Files.Combine(@"Work\D"));
        var workNode = Node(vm, work);
        Assert.Equal(new[] { "B", "D" }, ChildNames(workNode));

        _t.Files.Folder(@"Work\C");
        vm.GoUp();
        await Loaded(vm);
        vm.Navigate(_t.Files.Combine(@"Work\D"));
        await Loaded(vm);

        Assert.Equal(new[] { "B", "C", "D" }, ChildNames(workNode));
        // The red line runs down past B and C to D, and stops there.
        Assert.True(workNode.Children[0].PathLineTop && workNode.Children[0].PathLineBottom);
        Assert.True(workNode.Children[1].PathLineTop && workNode.Children[1].PathLineBottom);
        Assert.True(workNode.Children[2].PathLineTop && !workNode.Children[2].PathLineBottom);
    });

    [Fact]
    public void Going_to_a_folder_made_after_its_open_parent_was_read_selects_it() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(work);
        var node = Node(vm, work);
        node.IsExpanded = true;   // read now, and stays open
        string later = _t.Files.Folder(@"Work\Later");

        vm.Navigate(later);   // e.g. typed in the address bar
        await Loaded(vm);

        Assert.Equal(later, vm.Tree.Selected?.FullPath);
        Assert.Same(vm.Tree.Selected, vm.Tree.PathChain[^1]);
    });

    [Fact]
    public void Renaming_the_shown_folder_from_the_second_list_keeps_the_tree_on_it() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        string d = _t.Files.Combine(@"Work\D");
        var vm = await StartAsync(d);
        vm.SetDualPane(true, work);
        await Wait.Until(() => vm.Dual.Folder == work && vm.Dual.List.LoadingMessage == null && vm.Dual.List.View != null, "the second list");
        vm.SetActiveList(false);
        Assert.Equal(d, vm.Tree.Selected?.FullPath);

        // Only the case changes, so the main list's path still works while the tree gets a new node for it.
        Directory.Move(d, _t.Files.Combine(@"Work\d_tmp"));
        Directory.Move(_t.Files.Combine(@"Work\d_tmp"), _t.Files.Combine(@"Work\d"));
        _t.Watch.Changed(work);
        await Wait.Until(() => ChildNames(Node(vm, work)).Contains("d"), "the renamed folder in the tree");

        Assert.Equal("d", vm.Tree.Selected?.Name);
        Assert.Same(vm.Tree.Selected, vm.Tree.PathChain[^1]);
        Assert.Contains(Node(vm, work).Children, c => c == vm.Tree.Selected);
        Assert.Equal(d, vm.ActiveTab.FullPath, StringComparer.OrdinalIgnoreCase);
    });

    [Fact]
    public void A_folder_that_can_not_be_read_keeps_its_branch_when_opened_again() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(_t.Files.Combine("Empty"));
        var node = Node(vm, work);
        node.IsExpanded = true;
        node.IsExpanded = false;
        string moved = _t.Files.Combine("Work-away");
        Directory.Move(work, moved);   // as if a network drive dropped out: reading Work fails
        try
        {
            node.IsExpanded = true;
            Assert.Equal(new[] { "B", "D" }, ChildNames(node));
        }
        finally
        {
            Directory.Move(moved, work);
        }
    });

    [Fact]
    public void An_unopened_folder_that_loses_all_its_subfolders_loses_its_expander() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        string b = _t.Files.Combine(@"Work\B");
        var vm = await StartAsync(work);
        Node(vm, work).IsExpanded = true;
        var bNode = Node(vm, b);
        Assert.Single(bNode.Children);   // the expander placeholder: B was never opened
        Assert.Null(bNode.Children[0].FullPath);

        vm.SetDualPane(true, b);
        await Wait.Until(() => vm.Dual.Folder == b && vm.Dual.List.View != null && vm.Dual.List.LoadingMessage == null, "the second list");
        Directory.Delete(_t.Files.Combine(@"Work\B\deep"));
        _t.Watch.Changed(b);

        await Wait.Until(() => bNode.Children.Count == 0, "the expander to go");
    });

    [Fact]
    public void A_favorite_folder_and_its_place_under_this_pc_both_follow() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        _t.Favorites.Add(work);
        var vm = await StartAsync(work);
        vm.Tree.Build();
        vm.Tree.Sync(work);
        var nodes = vm.Tree.Roots.SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.IsFolder && string.Equals(n.FullPath, work, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, nodes.Count);
        nodes.ForEach(n => n.IsExpanded = true);

        _t.Files.Folder(@"Work\Both");
        _t.Watch.Changed(work);

        await Wait.Until(() => nodes.All(n => ChildNames(n).Contains("Both")), "both nodes to list the new folder");
    });

    [Fact]
    public void An_unchanged_folder_leaves_the_tree_alone() => WpfThread.Run(async () =>
    {
        string work = _t.Files.Combine("Work");
        var vm = await StartAsync(work);
        var node = Node(vm, work);
        node.IsExpanded = true;
        var before = node.Children.ToList();
        int changes = 0;
        node.Children.CollectionChanged += (_, _) => changes++;

        _t.Files.File(@"Work\another.txt");   // a file, not a folder
        _t.Watch.Changed(work);
        await Loaded(vm);
        await WpfThread.Settle();

        Assert.Equal(0, changes);
        Assert.Equal(before, node.Children);
    });
}
