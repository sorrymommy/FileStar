using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

public class BatchRenameViewModelTests
{
    static FileItem Item(string name, bool folder = false) => new()
    {
        Name = name,
        FullPath = @"C:\Work\" + name,
        IsDirectory = folder,
        RawExtension = folder ? "" : System.IO.Path.GetExtension(name),
        Modified = new DateTime(2026, 10, 27),
    };

    static BatchRenameViewModel Create(params FileItem[] items) => new(items, exists: _ => false);

    [Fact]
    public void Find_and_replace_keeps_the_extension()
    {
        var vm = Create(Item("draft report.docx"), Item("draft memo.txt"));
        vm.Find = "draft";
        vm.Replace = "final";
        Assert.Equal(new[] { "final report.docx", "final memo.txt" }, vm.Rows.Select(r => r.NewName));
        Assert.Equal("2개 바꾸기", vm.ApplyText);
    }

    [Fact]
    public void A_pattern_numbers_with_the_start_numbers_digits()
    {
        var vm = Create(Item("a.jpg"), Item("b.jpg"), Item("folder", folder: true));
        vm.PatternMode = true;
        vm.Pattern = "{name}_{n}_{date}";
        vm.Start = "009";
        Assert.Equal(new[] { "a_009_20261027.jpg", "b_010_20261027.jpg", "folder_011_20261027" }, vm.Rows.Select(r => r.NewName));
    }

    [Fact]
    public void Problems_are_skipped_and_explained()
    {
        var vm = new BatchRenameViewModel(new[] { Item("a.txt"), Item("b.txt"), Item("c.txt") },
            exists: path => path.EndsWith("taken.txt"));
        vm.PatternMode = true;
        vm.Pattern = "same";
        Assert.Equal(new string?[] { null, "이름이 겹침", "이름이 겹침" }, vm.Rows.Select(r => r.Problem));

        vm.Pattern = "bad:name";
        Assert.All(vm.Rows, r => Assert.Equal("쓸 수 없는 문자", r.Problem));
        Assert.Equal(0, vm.ChangingCount);
        Assert.Equal("문제가 있는 3개는 건너뜁니다.", vm.ProblemText);

        vm.Pattern = "taken";
        Assert.Equal("같은 이름이 이미 있음", vm.Rows[0].Problem);
    }

    [Fact]
    public void Swapping_names_within_the_batch_is_allowed()
    {
        var vm = new BatchRenameViewModel(new[] { Item("1.txt"), Item("2.txt") }, exists: _ => true);
        vm.PatternMode = true;
        vm.Pattern = "{n}";
        vm.Start = "2";
        Assert.Equal(new[] { "2.txt", "3.txt" }, vm.Rows.Select(r => r.NewName));
        Assert.Null(vm.Rows[0].Problem);
        Assert.Single(vm.Plan());
    }

    [Fact]
    public void The_extension_can_be_renamed_too()
    {
        var vm = Create(Item("a.jpeg"));
        vm.Find = "jpeg";
        vm.Replace = "jpg";
        Assert.Equal("a.jpeg", vm.Rows[0].NewName);
        vm.IncludeExtension = true;
        Assert.Equal("a.jpg", vm.Rows[0].NewName);
    }
}

public sealed class PropertiesViewModelTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_file_shows_its_general_facts_dates_and_attributes()
    {
        var vm = new PropertiesViewModel();
        vm.Show(new PropertiesTarget.Item(_dir.File("a.txt", "12345")));

        var sections = vm.Entries.OfType<PropertySection>().Select(s => s.Title).ToList();
        Assert.Equal(new[] { "일반", "날짜", "특성" }, sections.Take(3));
        Assert.True(vm.Entries.OfType<PropertySection>().First().IsFirst);
        Assert.Contains(vm.Entries.OfType<PropertyRow>(), r => r.Label == "크기" && r.Value.StartsWith("5 바이트"));
        Assert.Single(vm.Entries.OfType<PropertyFlags>());
    }

    [Fact]
    public void Hiding_every_row_of_a_section_hides_its_heading_too()
    {
        var vm = new PropertiesViewModel();
        int saves = 0;
        vm.SettingsChanged += () => saves++;
        vm.Show(new PropertiesTarget.Item(_dir.File("a.txt")));
        foreach (var label in new[] { "만든 날짜", "수정한 날짜", "액세스한 날짜" })
            vm.SetVisible(label, false);

        Assert.DoesNotContain(vm.Entries.OfType<PropertySection>(), s => s.Title == "날짜");
        Assert.Equal(3, saves);
        Assert.Contains(vm.ChooserItems(), c => c.Label == "만든 날짜" && !c.Visible);

        vm.ShowAll();
        Assert.Contains(vm.Entries.OfType<PropertySection>(), s => s.Title == "날짜");
    }

    [Fact]
    public async Task A_folder_offers_to_calculate_its_size()
    {
        string folder = _dir.Folder("big");
        _dir.File(@"big\a.bin", new string('x', 2048));
        _dir.File(@"big\sub\b.bin", "y");
        var vm = new PropertiesViewModel();
        vm.Show(new PropertiesTarget.Item(folder));

        var row = vm.Entries.OfType<FolderSizeRow>().Single();
        Assert.Null(row.Value);
        await ((FileStar.Mvvm.AsyncRelayCommand)row.CalculateCommand).ExecuteAsync();
        Assert.StartsWith("2 KB", row.Value);
        Assert.Contains("파일 2개, 폴더 1개", row.Value);
    }

    [Fact]
    public void A_selection_is_summed_up()
    {
        var vm = new PropertiesViewModel();
        vm.Show(new PropertiesTarget.Selection(new[]
        {
            new FileItem { Name = "a", FullPath = @"C:\a", Size = 1024 },
            new FileItem { Name = "b", FullPath = @"C:\b", IsDirectory = true },
        }));
        var rows = vm.Entries.OfType<PropertyRow>().ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("2개 (폴더 1, 파일 1)", rows["개수"]);
        Assert.Equal("1 KB (1,024 바이트)", rows["크기"]);
    }

    [Fact]
    public void The_name_column_stays_within_bounds() =>
        Assert.Equal(400, new PropertiesViewModel { LabelWidth = 9999 }.LabelWidth);
}

public class SearchPanelViewModelTests
{
    [Fact]
    public void A_search_tab_fills_the_panel()
    {
        var vm = new SearchPanelViewModel();
        vm.Show(SearchLocation.Find(@"C:\Work", "보고서", SearchFields.Name | SearchFields.Memo, subfolders: false));
        Assert.True(vm.IsVisible);
        Assert.Equal("보고서", vm.Query);
        Assert.True(vm.SearchName && vm.SearchMemo && !vm.SearchTag && !vm.Subfolders);
        Assert.Equal(0, vm.ScopeIndex);
        Assert.Equal("폴더: Work", vm.FolderLabel);
    }

    [Fact]
    public void Tag_lists_have_no_panel()
    {
        var vm = new SearchPanelViewModel();
        vm.Show(SearchLocation.Tag("예산"));
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void Changing_an_option_searches_again_only_when_there_is_a_word()
    {
        var vm = new SearchPanelViewModel();
        var runs = new List<SearchLocation>();
        vm.SearchRequested += runs.Add;
        vm.Show(SearchLocation.Find(@"C:\Work", "", SearchFields.Default, subfolders: true));

        vm.SearchExtension = true;
        Assert.Empty(runs);

        vm.Query = "pdf";
        vm.Subfolders = false;
        var search = Assert.Single(runs);
        Assert.False(search.Subfolders);
        Assert.True(search.Fields.HasFlag(SearchFields.Extension));
        Assert.Equal("netm", vm.OptionLetters);
    }

    [Fact]
    public void Everywhere_searches_need_tags_or_memos()
    {
        var vm = new SearchPanelViewModel();
        string? problem = null;
        vm.Problem += p => problem = p;
        vm.Show(SearchLocation.Find(@"C:\Work", "x", SearchFields.Name, subfolders: true));
        vm.ScopeIndex = 1;
        Assert.Equal("모든 위치에서는 태그나 메모에서만 찾을 수 있습니다.", problem);
        Assert.False(vm.FolderOptionsEnabled);
    }
}

public class CompareViewModelTests
{
    static FileItem Item(string name, long size) => new() { Name = name, FullPath = @"X:\" + name, Size = size, Modified = new DateTime(2026, 1, 1) };

    [Fact]
    public void The_bar_sums_up_both_sides()
    {
        var vm = new CompareViewModel { IsOn = true };
        vm.Recompute(@"C:\Left", new[] { Item("a", 1), Item("b", 1) }, @"D:\Right", new[] { Item("a", 1) });
        Assert.Equal("왼쪽에만 1 · 오른쪽에만 0 · 다름 0 · 같음 1", vm.Summary);
        Assert.True(vm.CanCopyRight);
        Assert.False(vm.CanCopyLeft);
    }

    [Fact]
    public void Same_items_hide_only_with_different_only()
    {
        var same = Item("a", 1);
        var vm = new CompareViewModel { IsOn = true };
        vm.Recompute(@"C:\L", new[] { same }, @"D:\R", new[] { Item("a", 1) });
        Assert.False(vm.Hides(same));
        vm.DiffOnly = true;
        Assert.True(vm.Hides(same));
        vm.IsOn = false;
        Assert.False(vm.DiffOnly);
        Assert.False(vm.Hides(same));
    }

    [Fact]
    public void Without_a_real_folder_on_the_left_nothing_is_compared()
    {
        var item = Item("a", 1);
        var vm = new CompareViewModel { IsOn = true };
        vm.Recompute(null, new[] { item }, @"D:\R", Array.Empty<FileItem>());
        Assert.Equal("비교하려면 왼쪽에 실제 폴더를 여세요.", vm.Summary);
        Assert.Equal(CompareState.None, item.CompareState);
    }

    [Fact]
    public void The_copy_question_mentions_what_gets_replaced()
    {
        var newer = Item("a", 1);
        newer.CompareState = CompareState.Newer;
        string question = CompareViewModel.CopyQuestion(toRight: true, new[] { newer, Item("b", 1) });
        Assert.Contains("왼쪽 → 오른쪽으로 2개", question);
        Assert.Contains("1개는 반대쪽의 더 오래된 파일을 대신합니다", question);
    }
}
