using System.Collections.Concurrent;
using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Services;

/// <summary>The real folder watcher, and how the memos follow the renames it reports.</summary>
public sealed class FolderWatcherTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;

    public FolderWatcherTests() => _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.Now.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.Now > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_swap_made_here_is_not_undone_when_the_watcher_reports_it()
    {
        string folder = _dir.Folder("work");
        string a = _dir.File(@"work\a.txt", "A"), b = _dir.File(@"work\b.txt", "B");
        _notes.Set(a, "A의 메모", Array.Empty<string>());
        _notes.Set(b, "B의 메모", Array.Empty<string>());
        var reported = new ConcurrentQueue<(string Old, string New)>();
        using (new FolderWatchService().Watch(folder, () => { }, (o, n) => reported.Enqueue((o, n))))
        {
            BatchRenamer.Apply(new[] { (a, "b.txt"), (b, "a.txt") }, _notes);
            await Until(() => reported.Count >= 4, "the four renames of a swap");
        }

        foreach (var (oldPath, newPath) in reported)
            _notes.FollowRename(oldPath, newPath);

        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("B의 메모", _notes.Get(a)?.Text);
        Assert.Equal("A의 메모", _notes.Get(b)?.Text);
    }

    [Fact]
    public async Task A_rename_made_by_another_program_takes_the_memo_along()
    {
        string folder = _dir.Folder("work");
        string a = _dir.File(@"work\a.txt"), c = _dir.Combine(@"work\c.txt");
        _notes.Set(a, "메모", Array.Empty<string>());
        var reported = new ConcurrentQueue<(string Old, string New)>();
        using (new FolderWatchService().Watch(folder, () => { }, (o, n) => reported.Enqueue((o, n))))
        {
            File.Move(a, c);
            await Until(() => !reported.IsEmpty, "the rename");
        }

        foreach (var (oldPath, newPath) in reported)
            _notes.FollowRename(oldPath, newPath);

        Assert.Equal("메모", _notes.Get(c)?.Text);
        Assert.Null(_notes.Get(a));
    }

    [Fact]
    public void A_memo_is_not_moved_onto_a_different_file()
    {
        string a = _dir.File("a.txt"), other = _dir.File("other.txt");
        _notes.Set(a, "메모", Array.Empty<string>());
        File.Delete(a);

        _notes.FollowRename(a, other);   // a reported rename that doesn't match the memo's file ID

        Assert.Equal("메모", _notes.Get(a)?.Text);
        Assert.Null(_notes.Get(other));
    }

    [Fact]
    public async Task Stopping_while_changes_pour_in_is_safe()
    {
        string folder = _dir.Folder("busy");
        int changed = 0;
        var watcher = new FolderWatchService().Watch(folder, () => Interlocked.Increment(ref changed));
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
                File.WriteAllText(Path.Combine(folder, $"{i % 5}.txt"), i.ToString());
        });
        await Task.Delay(30);
        watcher.Dispose();
        await writer;
        await Task.Delay(600);   // past the settle time: a disposed watcher reports nothing

        Assert.Equal(0, changed);
    }
}
