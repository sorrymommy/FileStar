using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>The "#" column: rows are numbered in the order the list shows them.</summary>
public sealed class FileListViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;

    public FileListViewModelTests()
    {
        _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));
        for (int i = 1; i <= 30; i++)
            _dir.File($@"work\file{i:D2}.txt");
    }

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    static void AssertNumberedInOrder(FileListViewModel list) =>
        Assert.Equal(Enumerable.Range(1, list.VisibleItems.Count()), list.VisibleItems.Select(i => i.RowNumber));

    [Fact]
    public void Rows_are_numbered_in_display_order_through_sorting_and_filtering() => WpfThread.Run(async () =>
    {
        var list = new FileListViewModel(new LocalFileSystem(), _notes);
        Assert.True(await list.LoadAsync(_dir.Combine("work")));
        AssertNumberedInOrder(list);
        Assert.Equal("file01.txt", list.VisibleItems.First().Name);

        list.SetSort("Name", descending: true);
        AssertNumberedInOrder(list);
        Assert.Equal("file30.txt", list.VisibleItems.First().Name);

        list.SetFilter(i => i.Name.EndsWith("5.txt") || i.Name.EndsWith("0.txt"));
        Assert.Equal(24, list.VisibleItems.Count());
        AssertNumberedInOrder(list);

        list.SetFilter(null);
        AssertNumberedInOrder(list);
    });

    [Fact]
    public void Search_results_added_as_they_are_found_are_numbered_too() => WpfThread.Run(async () =>
    {
        // More than one batch of results (100 each), sorted opposite to the order they are found in, so later
        // batches land in front of rows that were already numbered.
        for (int i = 1; i <= 250; i++)
            _dir.File($@"big\doc{i:D3}.txt");
        var list = new FileListViewModel(new LocalFileSystem(), _notes);
        list.SetSort("Name", descending: true);
        var search = SearchLocation.Find(_dir.Combine("big"), "doc", SearchFields.Name, subfolders: true);
        Assert.True(await list.LoadAsync(search.Path));
        await WpfThread.Settle();

        Assert.Equal(250, list.VisibleItems.Count());
        Assert.Equal("doc250.txt", list.VisibleItems.First().Name);
        AssertNumberedInOrder(list);
    });
}
