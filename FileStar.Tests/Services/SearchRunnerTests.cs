using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

public sealed class SearchRunnerTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    List<string> Run(SearchLocation search, params (string Path, FileNote Note)[] notes)
    {
        var found = new List<string>();
        SearchRunner.Run(search, notes.ToList(), FileAttributes.Hidden | FileAttributes.System, found.AddRange, CancellationToken.None);
        return found.Select(p => Path.GetRelativePath(_dir.Path, p)).OrderBy(p => p).ToList();
    }

    static FileNote Note(string text, params string[] tags) => new(text, DateTime.Now, tags.ToList());

    [Fact]
    public void A_tag_list_holds_everything_carrying_that_tag()
    {
        string a = _dir.File("a.txt"), b = _dir.File("b.txt");
        var result = Run(SearchLocation.Tag("예산"), (a, Note("", "예산")), (b, Note("", "다른")));
        Assert.Equal(new[] { "a.txt" }, result);
    }

    [Fact]
    public void Memos_are_searched_by_text_and_files_by_name()
    {
        string memo = _dir.File("memo.txt");
        _dir.File(@"sub\보고서 최종.docx");
        _dir.File("other.txt");
        var search = SearchLocation.Find(_dir.Path, "보고서", SearchFields.Default, subfolders: true);
        var result = Run(search, (memo, Note("분기 보고서 요약")));
        Assert.Equal(new[] { "memo.txt", @"sub\보고서 최종.docx" }, result);
    }

    [Fact]
    public void Extensions_match_with_or_without_dot_and_wildcard()
    {
        _dir.File("a.pdf");
        _dir.File("b.PDF");
        _dir.File("c.txt");
        foreach (var query in new[] { "pdf", ".pdf", "*.pdf" })
        {
            var search = SearchLocation.Find(_dir.Path, query, SearchFields.Extension, subfolders: false);
            Assert.Equal(new[] { "a.pdf", "b.PDF" }, Run(search));
        }
    }

    [Fact]
    public void Without_subfolders_only_the_folder_itself_is_searched()
    {
        _dir.File("top.log");
        _dir.File(@"sub\deep.log");
        var search = SearchLocation.Find(_dir.Path, "*.log", SearchFields.Name, subfolders: false);
        Assert.Equal(new[] { "top.log" }, Run(search));
    }

    [Fact]
    public void Hash_terms_look_only_at_tags()
    {
        string a = _dir.File("a.txt"), b = _dir.File("b.txt");
        var search = SearchLocation.Find(_dir.Path, "#예산", SearchFields.Default, subfolders: true);
        Assert.Equal(new[] { "a.txt" }, Run(search, (a, Note("", "2026예산")), (b, Note("예산 메모"))));
    }
}

public class FolderCompareTests
{
    static FileItem Item(string name, long? size, DateTime modified, bool folder = false) =>
        new() { Name = name, FullPath = @"X:\" + name, Size = size, Modified = modified, IsDirectory = folder };

    [Fact]
    public void Items_are_marked_against_the_other_side()
    {
        var t = new DateTime(2026, 1, 1);
        var left = new List<FileItem>
        {
            Item("same.txt", 10, t), Item("newer.txt", 10, t.AddHours(1)), Item("only-left.txt", 1, t),
            Item("size.txt", 1, t), Item("dir", null, t, folder: true),
        };
        var right = new List<FileItem>
        {
            Item("SAME.txt", 10, t.AddSeconds(1)), Item("newer.txt", 10, t), Item("only-right.txt", 1, t),
            Item("size.txt", 2, t), Item("dir", null, t.AddDays(3), folder: true),
        };

        var result = FolderCompare.Mark(left, right);

        Assert.Equal(new FolderCompare.Result(LeftOnly: 1, RightOnly: 1, LeftNewer: 1, RightNewer: 0, Different: 2, Same: 2), result);
        Assert.Equal(CompareState.Same, left[0].CompareState);
        Assert.Equal(CompareState.Newer, left[1].CompareState);
        Assert.Equal(CompareState.Older, right[1].CompareState);
        Assert.Equal(CompareState.OnlyHere, right[2].CompareState);
        Assert.Equal(CompareState.Different, left[3].CompareState);
        Assert.Equal(new[] { "newer.txt", "only-left.txt" }, FolderCompare.ToCopy(left).Select(i => i.Name));
    }
}
