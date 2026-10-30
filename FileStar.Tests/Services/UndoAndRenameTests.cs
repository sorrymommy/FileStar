using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

public sealed class UndoAndRenameTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;
    readonly UndoHistory _undo = new();
    readonly DirectFileOperations _files;

    public UndoAndRenameTests()
    {
        _notes = SqliteNotesStore.Open(_dir.Combine("data\\notes.db"));
        _files = new DirectFileOperations(_notes, _undo);
    }

    UndoContext Context => new(_notes, _files);

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task The_history_keeps_the_last_fifty_steps()
    {
        for (int i = 0; i < 60; i++)
            _undo.Push(new CreateUndo(new[] { _dir.Combine($"gone{i}") }, "새 폴더"));
        Assert.Equal("새 폴더 (gone59)", _undo.Peek!.Description);
        int undone = 0;
        while ((await _undo.UndoAsync(Context)).Action != null)
            undone++;
        Assert.Equal(50, undone);
    }

    [Fact]
    public async Task Undoing_a_rename_gives_the_old_name_and_memo_back()
    {
        string file = _dir.File("old.txt");
        _notes.Set(file, "메모", Array.Empty<string>());
        string renamed = _files.Rename(file, "new.txt");
        Assert.Equal("메모", _notes.Get(renamed)?.Text);

        var (action, problem) = await _undo.UndoAsync(Context);

        Assert.IsType<RenameUndo>(action);
        Assert.Null(problem);
        Assert.True(File.Exists(file));
        Assert.Equal("메모", _notes.Get(file)?.Text);
        Assert.Null(_undo.Peek);
    }

    [Fact]
    public async Task Undoing_a_move_brings_files_back()
    {
        string file = _dir.File("a.txt");
        string target = _dir.Folder("target");
        await _files.TransferAsync(new[] { file }, target, move: true);
        Assert.False(File.Exists(file));

        await _undo.UndoAsync(Context);

        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task A_rename_that_can_no_longer_be_undone_says_why()
    {
        string file = _dir.File("old.txt");
        string renamed = _files.Rename(file, "new.txt");
        File.Delete(renamed);
        var (_, problem) = await _undo.UndoAsync(Context);
        Assert.Contains("new.txt", problem);
    }

    [Fact]
    public async Task A_group_undoes_its_steps_in_reverse()
    {
        string a = _dir.File("a.txt");
        var first = new RenameUndo(a, _dir.Combine("b.txt"));
        var second = new RenameUndo(_dir.Combine("b.txt"), _dir.Combine("c.txt"));
        File.Move(a, _dir.Combine("c.txt"));
        _undo.Push(new GroupUndo(new UndoAction[] { first, second }, "두 번"));

        await _undo.UndoAsync(Context);

        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Batch_rename_swaps_names_without_tripping_over_itself()
    {
        string a = _dir.File("a.txt", "A"), b = _dir.File("b.txt", "B");
        _notes.Set(a, "A의 메모", Array.Empty<string>());

        var (done, failed) = BatchRenamer.Apply(new[] { (a, "b.txt"), (b, "a.txt") }, _notes);

        Assert.Empty(failed);
        Assert.Equal(2, done.Count);
        Assert.Equal("A", File.ReadAllText(b));
        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A의 메모", _notes.Get(b)?.Text);
        Assert.Null(_notes.Get(a));
    }

    [Fact]
    public async Task Undoing_a_swap_swaps_back()
    {
        string a = _dir.File("a.txt", "A"), b = _dir.File("b.txt", "B");
        _notes.Set(a, "A의 메모", Array.Empty<string>());
        var (done, _) = BatchRenamer.Apply(new[] { (a, "b.txt"), (b, "a.txt") }, _notes);
        _undo.Push(new BatchRenameUndo(done));

        var (action, problem) = await _undo.UndoAsync(Context);

        Assert.Equal("이름 한꺼번에 바꾸기 (2개)", action?.Description);
        Assert.Null(problem);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("A의 메모", _notes.Get(a)?.Text);
        Assert.Null(_notes.Get(b));
    }

    [Fact]
    public void Creating_a_folder_that_exists_fails_without_recording_undo()
    {
        _dir.Folder("taken");
        Assert.Throws<IOException>(() => _files.CreateFolder(_dir.Path, "taken"));
        Assert.Null(_undo.Peek);
    }
}
