using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>#156: the address bar shows path buttons until clicked, then the address as text.</summary>
public sealed class AddressBarTests : IDisposable
{
    readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    static List<(string Name, string Path)> Steps(string path) =>
        MainViewModel.BuildCrumbs(path).Select(c => (c.Name, c.FullPath)).ToList();

    [Fact]
    public void Local_folders_start_from_this_pc()
    {
        var steps = Steps(@"C:\Work\Sub");
        Assert.Equal((Locations.ThisPc, Locations.ThisPc), steps[0]);
        Assert.Equal(@"C:\", steps[1].Path);
        Assert.Equal(new[] { ("Work", @"C:\Work"), ("Sub", @"C:\Work\Sub") }, steps.Skip(2));
        Assert.Equal(new[] { (Locations.ThisPc, Locations.ThisPc) }, Steps(Locations.ThisPc));
    }

    [Fact]
    public void Network_folders_start_from_their_computer_and_wsl_from_the_distribution()
    {
        Assert.Equal(new[] { ("NAS", @"\\NAS"), ("share", @"\\NAS\share"), ("docs", @"\\NAS\share\docs") }, Steps(@"\\NAS\share\docs"));
        Assert.Equal(new[] { ("NAS", @"\\NAS") }, Steps(@"\\NAS"));
        Assert.Equal(new[] { ("Ubuntu", @"\\wsl.localhost\Ubuntu"), ("home", @"\\wsl.localhost\Ubuntu\home") },
            Steps(@"\\wsl.localhost\Ubuntu\home"));
    }

    [Fact]
    public void Leaving_the_address_without_enter_shows_where_the_active_list_is() => WpfThread.Run(async () =>
    {
        string other = _t.Files.Folder("Other");
        var vm = new MainViewModel(_t.Services, new[] { _t.Files.Path })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Wait.Until(() => vm.FileList.View != null, "the list to load");

        vm.AddressText = @"C:\half typed";
        vm.RestoreAddress();
        Assert.Equal(_t.Files.Path, vm.AddressText);

        // A path that isn't there: say so, and keep it in the bar (no focus move) to be corrected.
        var focus = new List<FocusTarget>();
        vm.FocusRequested += focus.Add;
        string missing = _t.Files.Combine("no such folder");
        vm.AddressText = missing;
        vm.CommitAddress();
        Assert.Contains(_t.Dialogs.Messages, m => m.StartsWith("warn:") && m.Contains("no such folder"));
        Assert.Equal(missing, vm.AddressText);
        Assert.Empty(focus);
        Assert.Equal(_t.Files.Path, vm.ActiveFolder);

        vm.SetDualPane(true, other);
        await Wait.Until(() => vm.Dual.List.View != null, "the second list");
        vm.SetActiveList(true);
        vm.AddressText = "something else";
        vm.RestoreAddress();
        Assert.Equal(other, vm.AddressText);
        Assert.Equal("Other", vm.Crumbs[^1].Name);
    });
}
