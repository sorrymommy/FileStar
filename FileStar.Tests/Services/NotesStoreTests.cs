using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

public sealed class NotesStoreTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _store;

    public NotesStoreTests() => _store = SqliteNotesStore.Open(_dir.Combine("notes.db"));

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void A_memo_is_kept_and_found_regardless_of_case()
    {
        string file = _dir.File("a.txt");
        _store.Set(file, "메모  ", new[] { "태그", "태그" });
        var note = _store.Get(file.ToUpperInvariant());
        Assert.Equal("메모", note?.Text);
        Assert.Equal(new[] { "태그" }, note?.TagList);
        Assert.Null(_store.StorageError);
    }

    [Fact]
    public void Clearing_text_and_tags_removes_the_memo()
    {
        string file = _dir.File("a.txt");
        _store.Set(file, "메모", Array.Empty<string>());
        Assert.Null(_store.Set(file, "", Array.Empty<string>()));
        Assert.Null(_store.Get(file));
        Assert.Empty(_store.Snapshot());
    }

    [Fact]
    public void Memos_survive_reopening_the_database()
    {
        string file = _dir.File("a.txt");
        _store.Set(file, "남는 메모", new[] { "x" });
        _store.Dispose();
        using var reopened = SqliteNotesStore.Open(_dir.Combine("notes.db"));
        Assert.Equal("남는 메모", reopened.Get(file)?.Text);
    }

    [Fact]
    public void Renaming_a_folder_moves_the_memos_inside_it()
    {
        string folder = _dir.Folder("old");
        string inside = _dir.File(@"old\deep\b.txt");
        _store.Set(folder, "폴더 메모", Array.Empty<string>());
        _store.Set(inside, "파일 메모", Array.Empty<string>());

        _store.Rename(folder, _dir.Combine("new"));

        Assert.Null(_store.Get(inside));
        Assert.Equal("폴더 메모", _store.Get(_dir.Combine("new"))?.Text);
        Assert.Equal("파일 메모", _store.Get(_dir.Combine(@"new\deep\b.txt"))?.Text);
    }

    [Fact]
    public void Copies_get_their_own_copy_of_the_memo()
    {
        string file = _dir.File("a.txt");
        _store.Set(file, "원본", Array.Empty<string>());
        _store.CarryOver(new[] { (file, _dir.Combine("copy.txt")) }, move: false);
        Assert.Equal("원본", _store.Get(file)?.Text);
        Assert.Equal("원본", _store.Get(_dir.Combine("copy.txt"))?.Text);
    }

    [Fact]
    public void A_folder_knows_when_something_inside_it_has_a_memo()
    {
        string file = _dir.File(@"a\b\c.txt");
        Assert.False(_store.FolderHasNotes(_dir.Combine("a")));
        _store.Set(file, "", new[] { "t" });
        Assert.True(_store.FolderHasNotes(_dir.Combine("a")));
        Assert.True(_store.FolderHasNotes(_dir.Combine(@"a\b\")));
        Assert.False(_store.FolderHasNotes(_dir.Combine(@"a\b\c.txt")));
    }

    [Fact]
    public void Tags_are_counted_alphabetically_and_suggested_by_use()
    {
        _store.Set(_dir.File("1.txt"), "", new[] { "나", "가" });
        _store.Set(_dir.File("2.txt"), "", new[] { "나" });
        Assert.Equal(new[] { ("가", 1), ("나", 2) }, _store.TagCounts());
        Assert.Equal(new[] { "나", "가" }, _store.AllTags());
    }

    [Fact]
    public void Confirming_a_memo_marks_it_current()
    {
        string file = _dir.File("a.txt");
        _store.Set(file, "메모", Array.Empty<string>());
        File.SetLastWriteTime(file, DateTime.Now.AddMinutes(5));
        Assert.True(_store.Get(file)!.IsStaleFor(File.GetLastWriteTime(file), false));
        var confirmed = _store.Confirm(file);
        Assert.NotNull(confirmed);
        Assert.Equal(confirmed, _store.Get(file));
    }

    [Fact]
    public void Every_change_is_announced()
    {
        int changes = 0;
        _store.Changed += () => changes++;
        string file = _dir.File("a.txt");
        _store.Set(file, "메모", Array.Empty<string>());
        _store.Rename(file, _dir.Combine("b.txt"));
        _store.Remove(new[] { _dir.Combine("b.txt") });
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Memos_from_the_old_json_file_are_imported_once()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("notes.json"),
            """{"C:\\Old\\a.txt": {"Text": "옛 메모", "Updated": "2025-01-01T00:00:00", "Tags": ["옛"]}}""");
        using var store = SqliteNotesStore.Open(dir.Combine("notes.db"));
        Assert.Equal("옛 메모", store.Get(@"C:\Old\a.txt")?.Text);
        Assert.True(File.Exists(dir.Combine("notes.json.migrated")));
    }
}
