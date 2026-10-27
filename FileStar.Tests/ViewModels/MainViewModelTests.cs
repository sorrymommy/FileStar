using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>The main window's ViewModel against a real temp folder, on the WPF test thread.</summary>
public sealed class MainViewModelTests : IDisposable
{
    readonly TestServices _t = new();

    public MainViewModelTests()
    {
        _t.Files.Folder("Alpha");
        _t.Files.File(@"Alpha\inner.txt");
        _t.Files.File("readme.txt", "hello");
        _t.Files.File("notes.md");
    }

    public void Dispose() => _t.Dispose();

    async Task<MainViewModel> StartAsync(string? folder = null)
    {
        var vm = new MainViewModel(_t.Services, new[] { folder ?? _t.Files.Path })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Loaded(vm);
        return vm;
    }

    static Task Loaded(MainViewModel vm) =>
        Wait.Until(() => vm.FileList.LoadingMessage == null && vm.FileList.View != null, "the list to load");

    static List<string> Names(MainViewModel vm) => vm.FileList.VisibleItems.Select(i => i.Name).ToList();

    static void Select(MainViewModel vm, params string[] names) =>
        vm.FileList.SetSelection(vm.FileList.VisibleItems.Where(i => names.Contains(i.Name)).ToList());

    [Fact]
    public void Starts_on_the_folder_it_was_given() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Assert.Equal(new[] { "Alpha", "notes.md", "readme.txt" }, Names(vm));
        Assert.Equal($"{_t.Files.Path} - FileStar", vm.Title);
        Assert.Equal(_t.Files.Path, vm.AddressText);
        Assert.Equal(Locations.ThisPc, vm.Crumbs[0].Name);
        Assert.StartsWith("3개 항목 (폴더 1, 파일 2)", vm.Status.Text);
        Assert.False(vm.CanGoBack);
        Assert.Contains(_t.Files.Path, _t.Recent.Folders);
    });

    [Fact]
    public void Back_forward_and_up_move_through_the_history() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.Navigate(_t.Files.Combine("Alpha"));
        await Loaded(vm);
        Assert.Equal(new[] { "inner.txt" }, Names(vm));
        Assert.True(vm.CanGoBack);

        vm.GoBack();
        await Loaded(vm);
        Assert.Equal(_t.Files.Path, vm.ActiveTab.FullPath);
        Assert.True(vm.CanGoForward);

        vm.GoForward();
        await Loaded(vm);
        vm.GoUp();
        await Loaded(vm);
        Assert.Equal(_t.Files.Path, vm.ActiveTab.FullPath);
    });

    [Fact]
    public void The_address_bar_goes_to_folders_and_selects_files() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        IReadOnlyList<FileItem>? selected = null;
        vm.FileList.SelectionRequested += rows => selected = rows;

        vm.AddressText = "Alpha\\inner.txt";
        vm.CommitAddress();
        await Loaded(vm);

        Assert.Equal(_t.Files.Combine("Alpha"), vm.ActiveTab.FullPath);
        Assert.Equal("inner.txt", selected?.Single().Name);
    });

    [Fact]
    public void A_missing_folder_is_reported_and_stays_put() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.Navigate(_t.Files.Combine("nowhere"));
        Assert.Contains(_t.Dialogs.Messages, m => m.Contains("폴더를 찾을 수 없습니다"));
        Assert.Equal(_t.Files.Path, vm.ActiveTab.FullPath);
    });

    [Fact]
    public void Pinned_tabs_stay_open() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.NewTab(_t.Files.Combine("Alpha"));
        await Loaded(vm);
        Assert.Equal(2, vm.Tabs.Count);
        var tab = vm.ActiveTab;
        vm.PinTabCommand.Execute(tab);

        vm.CloseTab(tab);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Contains("고정된 탭은 닫을 수 없습니다", vm.Status.Text);

        vm.PinTabCommand.Execute(tab);
        vm.CloseTab(tab);
        Assert.Single(vm.Tabs);
    });

    [Fact]
    public void A_new_folder_is_renamed_in_place_and_undone_step_by_step() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.NewFolder();
        await Wait.Until(() => vm.FileList.AllItems.Any(i => i.IsRenaming), "the new folder's rename box");
        var created = vm.FileList.AllItems.Single(i => i.IsRenaming);
        Assert.Equal("새 폴더", created.Name);

        vm.CommitRename(created, "프로젝트");
        await Wait.Until(() => Names(vm).Contains("프로젝트"), "the renamed folder");
        Assert.True(Directory.Exists(_t.Files.Combine("프로젝트")));
        Assert.Equal("실행 취소: 이름 바꾸기 (프로젝트 → 새 폴더)", vm.UndoHeader);

        await vm.UndoLastAsync();
        Assert.True(Directory.Exists(_t.Files.Combine("새 폴더")));
        await vm.UndoLastAsync();
        await Loaded(vm);
        Assert.DoesNotContain("새 폴더", Names(vm));
        Assert.Contains("되돌림: 새 폴더", vm.Status.Text);
    });

    [Fact]
    public void Names_with_forbidden_characters_are_refused() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        var row = vm.FileList.AllItems.Single(i => i.Name == "notes.md");
        vm.BeginRename(row);
        vm.CommitRename(row, "a:b.md");
        Assert.Contains(_t.Dialogs.Messages, m => m.Contains("사용할 수 없는 문자"));
        Assert.True(row.IsRenaming);
        Assert.True(File.Exists(_t.Files.Combine("notes.md")));
    });

    [Fact]
    public void A_saved_memo_shows_in_the_list_and_the_folder_gets_its_dot() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.IsInfoPaneVisible = true;
        Select(vm, "readme.txt");
        Assert.Equal("readme.txt", vm.InfoHeader.Title);

        vm.Notes.BeginEdit();
        vm.Notes.Text = "첫 줄\n둘째 줄";
        vm.Notes.SaveEdit();

        var row = vm.FileList.AllItems.Single(i => i.Name == "readme.txt");
        Assert.Equal("첫 줄", row.NoteSummary);
        Assert.Equal(NoteMark.Memo, row.MarkKind);
        Assert.True(_t.Notes.FolderHasNotes(_t.Files.Path));
    });

    [Fact]
    public void A_memo_search_finds_the_file_in_a_tab_of_its_own() => WpfThread.Run(async () =>
    {
        _t.Notes.Set(_t.Files.Combine("notes.md"), "분기 보고서", new[] { "예산" });
        var vm = await StartAsync();

        vm.OpenSearch();
        Assert.Equal(2, vm.Tabs.Count);
        Assert.True(vm.Search.IsVisible);
        vm.Search.Query = "보고서";
        vm.Search.Run();
        await Wait.Until(() => vm.FileList.LoadingMessage == null && Names(vm).Count > 0, "search results");

        Assert.Equal(new[] { "notes.md" }, Names(vm));
        Assert.True(vm.IsResultList);
        Assert.True(vm.ListNoteMarks);
        Assert.Contains("'보고서'", vm.Status.Text);

        vm.Navigate(SearchLocation.Tag("예산").Path);
        await Wait.Until(() => vm.FileList.LoadingMessage == null && Names(vm).Count > 0, "tag results");
        Assert.Equal("#예산", vm.ActiveTab.Title);
        Assert.False(vm.Search.IsVisible);
    });

    [Fact]
    public void Hidden_files_show_only_when_asked() => WpfThread.Run(async () =>
    {
        string hidden = _t.Files.File("secret.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var vm = await StartAsync();
        Assert.DoesNotContain("secret.txt", Names(vm));

        vm.ShowHidden = true;
        await Wait.Until(() => Names(vm).Contains("secret.txt"), "the hidden file");
        Assert.True(vm.FileList.AllItems.Single(i => i.Name == "secret.txt").IsHidden);
    });

    [Fact]
    public void Sorting_by_size_starts_largest_first_and_flips() => WpfThread.Run(async () =>
    {
        _t.Files.File("big.bin", new string('x', 5000));
        var vm = await StartAsync();
        vm.SortBy("Size");
        Assert.True(vm.FileList.SortDescending);
        Assert.Equal("big.bin", Names(vm)[1]);   // folders stay first
        vm.SortBy("Size");
        Assert.False(vm.FileList.SortDescending);
        vm.SortBy("Index");
        Assert.Equal("Size", vm.FileList.SortKey);
    });

    [Fact]
    public void The_session_is_saved_and_restored() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        vm.NewTab(_t.Files.Combine("Alpha"));
        await Loaded(vm);
        vm.ShowNoteMarks = false;
        vm.SortBy("Modified");
        vm.SaveSettings();

        var saved = _t.Settings.Load();
        Assert.Equal(new[] { _t.Files.Path, _t.Files.Combine("Alpha") }, saved.Tabs);
        Assert.Equal(1, saved.ActiveTab);
        Assert.False(saved.ShowNoteMarks);
        Assert.Equal("Modified", saved.SortKey);
        Assert.Equal(800, saved.Width);

        var restored = new MainViewModel(_t.Services, Array.Empty<string>());
        restored.Start();
        await Loaded(restored);
        Assert.Equal(_t.Files.Combine("Alpha"), restored.ActiveTab.FullPath);
        Assert.False(restored.ShowNoteMarks);
        Assert.Equal("Modified", restored.FileList.SortKey);
    });

    [Fact]
    public void The_second_list_compares_against_the_first() => WpfThread.Run(async () =>
    {
        var other = _t.Files.Folder("Other");
        File.Copy(_t.Files.Combine("readme.txt"), Path.Combine(other, "readme.txt"));
        File.SetLastWriteTime(Path.Combine(other, "readme.txt"), File.GetLastWriteTime(_t.Files.Combine("readme.txt")));
        var vm = await StartAsync();

        vm.SetDualPane(true, other);
        await Wait.Until(() => vm.Dual.List.View != null && vm.Dual.List.LoadingMessage == null, "the second list");
        vm.IsCompareOn = true;

        Assert.StartsWith("왼쪽에만 3 · 오른쪽에만 0 · 다름 0 · 같음 1", vm.Compare.Summary);
        Assert.Equal(CompareState.Same, vm.FileList.AllItems.Single(i => i.Name == "readme.txt").CompareState);

        vm.SetActiveList(true);
        Assert.Equal(other, vm.AddressText);
        Assert.Equal(other, vm.ActiveFolder);

        vm.IsDualVisible = false;
        Assert.False(vm.IsCompareOn);
        Assert.All(vm.FileList.AllItems, i => Assert.Equal(CompareState.None, i.CompareState));
    });

    [Fact]
    public void Favorites_follow_the_star_and_the_menu() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Assert.False(vm.IsFavorite);
        Assert.Null(vm.FavoriteButtonClicked());
        Assert.True(vm.IsFavorite);
        Assert.Contains(vm.FavoritesMenu, m => m.ToolTip == _t.Files.Path);

        vm.ToggleFavorite();
        Assert.False(vm.IsFavorite);
    });

    [Fact]
    public void AI_summaries_start_from_the_selection() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        await vm.RunAiSummaryAsync();
        Assert.Contains(_t.Dialogs.Messages, m => m.StartsWith("info: AI로 요약할 파일이나 폴더를"));

        Select(vm, "readme.txt");
        await vm.RunAiSummaryAsync();
        Assert.Equal("ai summary: " + _t.Files.Combine("readme.txt"), _t.Windows.Opened.Last());
    });

    [Fact]
    public void Batch_rename_follows_the_plan_and_undoes_as_one() => WpfThread.Run(async () =>
    {
        var vm = await StartAsync();
        Select(vm, "notes.md", "readme.txt");
        _t.Windows.BatchPlan = new[] { (_t.Files.Combine("notes.md"), "1.md"), (_t.Files.Combine("readme.txt"), "2.txt") };

        vm.BatchRenameCommand.Execute(null);
        await Wait.Until(() => Names(vm).Contains("1.md"), "the renamed files");
        Assert.StartsWith("실행 취소: 이름 한꺼번에 바꾸기 (2개)", vm.UndoHeader);

        await vm.UndoLastAsync();
        Assert.True(File.Exists(_t.Files.Combine("notes.md")));
        Assert.True(File.Exists(_t.Files.Combine("readme.txt")));
    });
}
