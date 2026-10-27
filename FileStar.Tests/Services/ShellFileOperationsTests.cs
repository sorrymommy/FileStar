using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

/// <summary>Windows' own file engine, run silently (no progress or conflict dialogs, nothing sent to the Recycle Bin).</summary>
public sealed class ShellFileOperationsTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;
    readonly UndoHistory _undo = new();
    readonly ShellFileOperations _files;

    public ShellFileOperationsTests()
    {
        _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));
        _files = new ShellFileOperations(_notes, _undo, new WindowOwner(), silent: true);
    }

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task A_copy_into_its_own_folder_gets_a_new_name_and_the_memo()
    {
        string file = _dir.File(@"work\a.txt");
        _notes.Set(file, "메모", Array.Empty<string>());

        Assert.True(await _files.TransferAsync(new[] { file }, _dir.Combine("work"), move: false));

        string copy = Directory.GetFiles(_dir.Combine("work")).Single(f => f != file);
        Assert.StartsWith("a ", Path.GetFileName(copy));
        Assert.Equal("메모", _notes.Get(copy)?.Text);
        Assert.IsType<CreateUndo>(_undo.Peek);
    }

    [Fact]
    public async Task A_move_takes_the_memo_along_and_can_be_undone()
    {
        string file = _dir.File(@"from\a.txt");
        string target = _dir.Folder("to");
        _notes.Set(file, "메모", new[] { "t" });

        Assert.True(await _files.TransferAsync(new[] { file }, target, move: true));

        string moved = Path.Combine(target, "a.txt");
        Assert.True(File.Exists(moved));
        Assert.Equal("메모", _notes.Get(moved)?.Text);
        Assert.Null(_notes.Get(file));
        await _undo.UndoAsync(new UndoContext(_notes, _files));
        Assert.True(File.Exists(file));
        Assert.Equal("메모", _notes.Get(file)?.Text);
    }

    [Fact]
    public async Task Moving_into_the_folder_it_is_already_in_does_nothing()
    {
        string file = _dir.File("a.txt");
        Assert.False(await _files.TransferAsync(new[] { file }, _dir.Path, move: true));
        Assert.Null(_undo.Peek);
    }

    [Fact]
    public async Task A_permanent_delete_is_not_undoable()
    {
        string file = _dir.File("a.txt");
        Assert.True(await _files.DeleteAsync(new[] { file }, permanently: true));
        Assert.False(File.Exists(file));
        Assert.Null(_undo.Peek);
    }
}
