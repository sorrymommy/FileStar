using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>
/// #153: files cut to the clipboard are drawn faded until they are pasted. #154: the toolbar's 새 폴더 · 복사 ·
/// 잘라내기 · 붙여넣기 act on the list being used, the right one too in 두 창 보기.
/// </summary>
public sealed class CutAndToolbarTests : IDisposable
{
    readonly TestServices _t = new();

    public CutAndToolbarTests()
    {
        _t.Files.Folder("Alpha");
        _t.Files.File("readme.txt", "hello");
        _t.Files.File("notes.md");
    }

    public void Dispose() => _t.Dispose();

    async Task<MainViewModel> StartAsync()
    {
        var vm = new MainViewModel(_t.Services, new[] { _t.Files.Path })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Loaded(vm);
        return vm;
    }

    static Task Loaded(MainViewModel vm) =>
        Wait.Until(() => vm.FileList.LoadingMessage == null && vm.FileList.View != null, "the list to load");

    static void Select(FileListViewModel list, params string[] names) =>
        list.SetSelection(list.VisibleItems.Where(i => names.Contains(i.Name)).ToList());

    static List<string> CutNames(FileListViewModel list) => list.AllItems.Where(i => i.IsCut).Select(i => i.Name).ToList();

    [Fact]
    public void Cut_files_stay_faded_through_a_reload_until_they_are_pasted() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Select(vm.FileList, "readme.txt");

        vm.CutActiveCommand.Execute(null);
        vm.OnClipboardChanged();   // the window calls this on every clipboard change
        Assert.Equal(new[] { "readme.txt" }, CutNames(vm.FileList));

        vm.Refresh(reloadTree: false);
        await Loaded(vm);
        Assert.Equal(new[] { "readme.txt" }, CutNames(vm.FileList));

        vm.Navigate(_t.Files.Combine("Alpha"));
        await Loaded(vm);
        await vm.PasteIntoCurrentFolderAsync();
        vm.OnClipboardChanged();
        await Loaded(vm);
        Assert.True(File.Exists(_t.Files.Combine(@"Alpha\readme.txt")));
        Assert.Empty(CutNames(vm.FileList));
        Assert.False(vm.PasteActiveCommand.CanExecute(null));   // a paste after a cut empties the clipboard
    });

    [Fact]
    public void Copying_something_else_ends_the_fading() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Select(vm.FileList, "readme.txt", "notes.md");
        vm.CutActiveCommand.Execute(null);
        vm.OnClipboardChanged();
        Assert.Equal(2, CutNames(vm.FileList).Count);

        Select(vm.FileList, "notes.md");
        vm.CopyActiveCommand.Execute(null);
        vm.OnClipboardChanged();
        Assert.Empty(CutNames(vm.FileList));
    });

    [Fact]
    public void Toolbar_buttons_need_a_selection_or_something_to_paste() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Select(vm.FileList);
        Assert.False(vm.CutActiveCommand.CanExecute(null));
        Assert.False(vm.CopyActiveCommand.CanExecute(null));
        Assert.False(vm.PasteActiveCommand.CanExecute(null));
        Assert.True(vm.NewFolderCommand.CanExecute(null));

        Select(vm.FileList, "notes.md");
        Assert.True(vm.CopyActiveCommand.CanExecute(null));
        vm.CopyActiveCommand.Execute(null);
        Assert.True(vm.PasteActiveCommand.CanExecute(null));

        vm.Navigate(Locations.ThisPc);
        await Loaded(vm);
        Assert.False(vm.PasteActiveCommand.CanExecute(null));   // 내 PC isn't a folder to paste into
        Assert.False(vm.NewFolderCommand.CanExecute(null));
    });

    [Fact]
    public void In_two_lists_the_toolbar_acts_on_the_one_being_used() => WpfThread.Run(async () =>
    {
        string other = _t.Files.Folder("Other");
        _t.Files.File(@"Other\right.txt");
        var vm = await StartAsync();
        vm.SetDualPane(true, other);
        await Wait.Until(() => vm.Dual.List.View != null && vm.Dual.List.LoadingMessage == null, "the second list");
        Select(vm.FileList, "readme.txt");
        vm.SetActiveList(true);

        Assert.False(vm.CopyActiveCommand.CanExecute(null));   // the right list has nothing selected
        Select(vm.Dual.List, "right.txt");
        vm.CutActiveCommand.Execute(null);
        vm.OnClipboardChanged();
        Assert.Equal(new[] { Path.Combine(other, "right.txt") }, _t.FileOps.ClipboardPaths);
        Assert.Equal(new[] { "right.txt" }, CutNames(vm.Dual.List));

        vm.SetActiveList(false);
        await vm.PasteActiveCommand.ExecuteAsync();
        await Loaded(vm);
        Assert.True(File.Exists(_t.Files.Combine("right.txt")));
        Assert.False(File.Exists(Path.Combine(other, "right.txt")));
    });
}

static class CommandExtensions
{
    /// <summary>Runs an <see cref="AsyncRelayCommand"/> and waits for it, as a click would (without waiting).</summary>
    public static Task ExecuteAsync(this System.Windows.Input.ICommand command) => ((FileStar.Mvvm.AsyncRelayCommand)command).ExecuteAsync();
}
