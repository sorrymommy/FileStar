namespace FileStar.Tests.Models;

public class FileNoteTests
{
    static readonly DateTime Written = new(2026, 10, 1, 12, 0, 0);

    [Fact]
    public void Summary_is_the_first_non_empty_line()
    {
        var note = new FileNote("\n  \n  첫 줄 요약  \n- 둘째", Written);
        Assert.Equal("첫 줄 요약", note.Summary);
    }

    [Fact]
    public void A_memo_is_stale_once_the_file_is_saved_more_than_two_seconds_after_it()
    {
        var note = new FileNote("메모", Written);
        Assert.False(note.IsStaleFor(Written.AddSeconds(2), isDirectory: false));
        Assert.True(note.IsStaleFor(Written.AddSeconds(3), isDirectory: false));
    }

    [Fact]
    public void Folders_and_tag_only_notes_never_go_stale()
    {
        Assert.False(new FileNote("메모", Written).IsStaleFor(Written.AddDays(1), isDirectory: true));
        Assert.False(new FileNote("", Written, new() { "태그" }).IsStaleFor(Written.AddDays(1), isDirectory: false));
    }

    [Theory]
    [InlineData("#예산, 보고서", new[] { "예산", "보고서" })]
    [InlineData(" a ; b;;#c ", new[] { "a", "b", "c" })]
    [InlineData("  ", new string[0])]
    public void Tags_are_split_on_commas_semicolons_and_hashes(string input, string[] expected) =>
        Assert.Equal(expected, NoteTags.Parse(input));
}

public class SearchLocationTests
{
    [Fact]
    public void A_folder_search_survives_the_round_trip_through_its_path()
    {
        var search = SearchLocation.Find(@"C:\Work", "보고서", SearchFields.Name | SearchFields.Extension, subfolders: false);
        var parsed = SearchLocation.TryParse(search.Path);
        Assert.Equal(search, parsed);
    }

    [Fact]
    public void A_search_without_a_folder_looks_only_at_memos_and_tags()
    {
        var search = SearchLocation.Find(null, " 예산 ", SearchFields.Default, subfolders: false);
        Assert.Equal(SearchKind.Notes, search.Kind);
        Assert.Equal("예산", search.Query);
        Assert.Equal(SearchFields.Tag | SearchFields.Memo, search.Fields);
    }

    [Fact]
    public void Tabs_saved_before_search_options_still_open()
    {
        var parsed = SearchLocation.TryParse(@"search:here|C:\Work|보고서");
        Assert.NotNull(parsed);
        Assert.Equal("보고서", parsed!.Query);
        Assert.Equal(SearchFields.Default, parsed.Fields);
    }

    [Fact]
    public void Tag_lists_are_titled_with_the_tag()
    {
        Assert.Equal("#예산", SearchLocation.Tag("예산").Title);
        Assert.Null(SearchLocation.TryParse(@"C:\Work"));
    }
}

public class FavoriteTreeTests
{
    [Fact]
    public void Groups_are_listed_depth_first_with_their_path()
    {
        var outer = Favorite.Group("업무");
        var inner = Favorite.Group("2026");
        outer.Children.Add(inner);
        var labels = FavoriteTree.Groups(new[] { outer }).Select(g => g.Label).ToList();
        Assert.Equal(new[] { "업무", "업무 › 2026" }, labels);
    }

    [Fact]
    public void Locate_finds_the_owning_group()
    {
        var group = Favorite.Group("업무");
        var item = Favorite.Folder(@"C:\Work", "Work");
        group.Children.Add(item);
        var items = new System.Collections.ObjectModel.ObservableCollection<Favorite> { group };
        var location = FavoriteTree.Locate(items, item);
        Assert.Same(group, location?.Owner);
        Assert.Same(group.Children, location?.List);
    }
}
