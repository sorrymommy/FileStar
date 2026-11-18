using System.IO;
using FileStar.Platform;
using FileStar.Tests.Support;

namespace FileStar.Tests.ViewModels;

/// <summary>
/// A folder's rows go on screen before their shell icons and type names, which arrive afterwards from a background
/// thread (a folder of .exe files takes seconds, as each has its own icon).
/// </summary>
public sealed class ShellInfoLoadingTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly SqliteNotesStore _notes;

    public ShellInfoLoadingTests() => _notes = SqliteNotesStore.Open(_dir.Combine(@"data\notes.db"));

    public void Dispose()
    {
        _notes.Dispose();
        _dir.Dispose();
    }

    // Each test gets extensions the shell has never been asked about in this process, so nothing is cached yet.
    static string FreshExtension() => ".t" + Guid.NewGuid().ToString("N")[..8];

    static bool IconCached(FileItem row) => ShellInfo.TryGetCachedIcon(row.FullPath, row.IsDirectory, row.ExactIcon, out _);

    [Fact]
    public void A_deferred_row_reads_only_the_cache_until_its_shell_info_arrives() => WpfThread.Run(async () =>
    {
        string ext = FreshExtension();
        var exe = FileItem.From(new FileInfo(_dir.File("tool.exe")))!;
        var other = FileItem.From(new FileInfo(_dir.File("data" + ext)))!;
        exe.DeferShellInfo();
        other.DeferShellInfo();
        var changed = new List<string?>();
        exe.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Null(exe.Icon);
        Assert.False(IconCached(exe));   // reading Icon did not ask the shell
        Assert.Equal("", other.TypeName);
        Assert.False(ShellInfo.TryGetCachedTypeName(ext, false, out _));

        await ShellInfo.PrefetchAsync(new[] { exe.ShellKey, other.ShellKey });
        exe.ShellInfoArrived();
        other.ShellInfoArrived();

        Assert.Contains(nameof(FileItem.Icon), changed);
        Assert.NotNull(exe.Icon);
        Assert.Equal(ShellInfo.GetTypeName(ext, false), other.TypeName);
        Assert.NotEqual("", other.TypeName);
    });

    [Fact]
    public void A_row_that_showed_a_blank_icon_is_told_even_if_the_header_read_it_from_the_cache_first() => WpfThread.Run(async () =>
    {
        var exe = FileItem.From(new FileInfo(_dir.File("raced.exe")))!;
        exe.DeferShellInfo();
        var changed = new List<string?>();
        exe.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(exe.Icon);   // the list cell binds a blank

        await ShellInfo.PrefetchAsync(new[] { exe.ShellKey });
        Assert.NotNull(exe.Icon);   // the info header reads it before the list hears it arrived
        exe.ShellInfoArrived();

        Assert.Contains(nameof(FileItem.Icon), changed);
    });

    [Fact]
    public void A_row_the_fetch_never_reached_asks_the_shell_itself_once_released() => WpfThread.Run(() =>
    {
        // What the list does when the shell fails partway: release the rest instead of leaving them blank.
        string ext = FreshExtension();
        var exe = FileItem.From(new FileInfo(_dir.File("stuck.exe")))!;
        var doc = FileItem.From(new FileInfo(_dir.File("stuck" + ext)))!;
        exe.DeferShellInfo();
        doc.DeferShellInfo();
        Assert.Null(exe.Icon);
        Assert.Equal("", doc.TypeName);

        exe.ShellInfoArrived();
        doc.ShellInfoArrived();

        Assert.NotNull(exe.Icon);
        Assert.NotEqual("", doc.TypeName);
        return Task.CompletedTask;
    });

    [Fact]
    public void A_row_that_is_not_deferred_still_fetches_its_own_icon() => WpfThread.Run(() =>
    {
        // Search results and other rows outside a folder load keep the old behavior.
        var exe = FileItem.From(new FileInfo(_dir.File("plain.exe")))!;
        Assert.NotNull(exe.Icon);
        Assert.NotEqual("", exe.TypeName);
        return Task.CompletedTask;
    });

    [Fact]
    public void Prefetch_reports_type_names_first_then_icons_up_to_the_last_item() => WpfThread.Run(async () =>
    {
        var keys = Enumerable.Range(1, 40)
            .Select(i => FileItem.From(new FileInfo(_dir.File($@"p\app{i:D2}.exe")))!.ShellKey)
            .ToList();
        var reports = new List<int>();
        var progress = new SyncProgress(reports);

        await ShellInfo.PrefetchAsync(keys, progress);

        Assert.Equal(0, reports[0]);
        Assert.Equal(keys.Count, reports[^1]);
        Assert.Equal(reports.OrderBy(r => r), reports);
        Assert.All(keys, k => Assert.True(ShellInfo.TryGetCachedIcon(k.Path, k.IsDirectory, k.ExactIcon, out _)));
    });

    [Fact]
    public void A_cancelled_prefetch_stops_without_reporting() => WpfThread.Run(async () =>
    {
        var key = FileItem.From(new FileInfo(_dir.File("never.exe")))!.ShellKey;
        var reports = new List<int>();

        await ShellInfo.PrefetchAsync(new[] { key }, new SyncProgress(reports), new CancellationToken(canceled: true));

        Assert.Empty(reports);
        Assert.False(ShellInfo.TryGetCachedIcon(key.Path, key.IsDirectory, key.ExactIcon, out _));
    });

    [Fact]
    public void A_folder_is_shown_without_waiting_for_icons_which_then_fill_in() => WpfThread.Run(async () =>
    {
        for (int i = 1; i <= 300; i++)
            _dir.File($@"many\app{i:D3}.exe");
        var list = new FileListViewModel(new LocalFileSystem(), _notes);
        int updates = 0;
        list.ShellInfoUpdated += () => updates++;

        Assert.True(await list.LoadAsync(_dir.Combine("many")));

        // The rows are there right away; 300 per-file icons can't all be fetched before LoadAsync returns.
        Assert.Equal(300, list.VisibleItems.Count());
        Assert.Null(list.LoadingMessage);
        Assert.Contains(list.AllItems, r => !IconCached(r));

        await Wait.Until(() => list.AllItems.All(IconCached) && updates > 0, "the icons to arrive");
        await WpfThread.Settle();
        Assert.All(list.AllItems, r => Assert.NotNull(r.Icon));
        Assert.All(list.AllItems, r => Assert.NotEqual("", r.TypeName));
    });

    [Fact]
    public void Sorting_by_type_has_every_type_name_before_the_rows_are_shown() => WpfThread.Run(async () =>
    {
        string a = FreshExtension(), b = FreshExtension(), c = FreshExtension();
        foreach (var name in new[] { "one" + c, "two" + a, "three" + b, "four" + a })
            _dir.File(@"typed\" + name);
        var list = new FileListViewModel(new LocalFileSystem(), _notes);
        list.SetSort("Type", descending: false);

        Assert.True(await list.LoadAsync(_dir.Combine("typed")));

        var types = list.VisibleItems.Select(r => r.TypeName).ToList();
        Assert.DoesNotContain("", types);
        Assert.Equal(types.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase), types);
    });

    [Fact]
    public void Leaving_a_folder_stops_filling_in_its_rows() => WpfThread.Run(async () =>
    {
        for (int i = 1; i <= 300; i++)
            _dir.File($@"left\app{i:D3}.exe");
        _dir.File(@"next\a.txt");
        var list = new FileListViewModel(new LocalFileSystem(), _notes);
        Assert.True(await list.LoadAsync(_dir.Combine("left")));
        var leftRows = list.AllItems.ToList();
        int updates = 0;
        list.ShellInfoUpdated += () => updates++;

        Assert.True(await list.LoadAsync(_dir.Combine("next")));
        await Wait.Until(() => list.AllItems.All(IconCached), "the new folder's icons");
        await WpfThread.Settle(300);

        Assert.Equal(new[] { "a.txt" }, list.AllItems.Select(r => r.Name));
        // Only the new folder's reports reached the list; the old folder's fetch stopped short of its last row.
        Assert.False(IconCached(leftRows[^1]));
        Assert.InRange(updates, 1, 3);
    });

    /// <summary>A selected row's info header picks up the icon and type name once they arrive.</summary>
    [Fact]
    public void The_info_header_of_a_selected_row_follows_its_icon() => WpfThread.Run(async () =>
    {
        using var t = new TestServices();
        string ext = FreshExtension();
        for (int i = 1; i <= 200; i++)
            t.Files.File($"app{i:D3}.exe");
        t.Files.File("zzz" + ext);
        var vm = new MainViewModel(t.Services, new[] { t.Files.Path })
        {
            LayoutProvider = () => new WindowLayout(10, 20, 800, 600, false, 250, 300, 0.5, new()),
        };
        vm.Start();
        await Wait.Until(() => vm.FileList.LoadingMessage == null && vm.FileList.View != null, "the list to load");

        var last = vm.FileList.VisibleItems.Last();
        vm.IsInfoPaneVisible = true;
        vm.FileList.SetSelection(new[] { last });
        Assert.Equal(last.Name, vm.InfoHeader.Title);

        await Wait.Until(() => vm.InfoHeader.Icon != null && vm.InfoHeader.Subtitle.StartsWith(last.TypeName) && last.TypeName != "",
            "the header to show the icon and type");
        Assert.Equal(last.Description, vm.InfoHeader.Subtitle);
    });

    /// <summary>Records reports on the reporting thread, so the test sees them all once the prefetch is done.</summary>
    sealed class SyncProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (reports)
                reports.Add(value);
        }
    }
}
