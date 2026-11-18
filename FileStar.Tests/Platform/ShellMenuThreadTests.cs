using System.Windows;
using FileStar.Platform;
using FileStar.Tests.Support;

namespace FileStar.Tests.Platform;

/// <summary>
/// Explorer's right-click menus are built on their own thread, so the UI thread (and FileStar's own menu,
/// which builds the Explorer menu ahead of time while it is open) never waits on shell extensions.
/// </summary>
public sealed class ShellMenuThreadTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly ShellMenuThread _menus = new();

    public void Dispose()
    {
        _menus.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void A_menu_is_built_off_the_calling_thread_and_freed_on_release() => WpfThread.Run(async () =>
    {
        string file = _dir.File("doc.txt");
        int caller = Environment.CurrentManagedThreadId;

        var task = _menus.PrepareAsync(IntPtr.Zero, new[] { file }, background: false, extended: false);
        var menu = await task;

        Assert.Equal(caller, Environment.CurrentManagedThreadId);   // the caller resumes on its own thread
        Assert.NotEqual(caller, await _menus.ThreadIdAsync());      // the menu was built on the shell thread
        Assert.Equal(new[] { file }, menu.Paths);
        Assert.False(menu.IsBackground);
        Assert.NotEqual(IntPtr.Zero, menu.HMenu);

        _menus.Release(task);
        // Freed on the shell thread: the menu handle first, then the COM object.
        await Wait.Until(() => menu.HMenu == IntPtr.Zero && menu.Menu == null, "the menu to be freed");
    });

    [Fact]
    public void A_folder_background_menu_is_built_for_the_folder() => WpfThread.Run(async () =>
    {
        var task = _menus.PrepareAsync(IntPtr.Zero, new[] { _dir.Path }, background: true, extended: true);
        var menu = await task;

        Assert.True(menu.IsBackground);
        Assert.True(menu.Extended);
        Assert.NotEqual(IntPtr.Zero, menu.HMenu);
        _menus.Release(task);
    });

    [Fact]
    public void A_menu_for_a_missing_file_fails_and_releasing_it_is_harmless() => WpfThread.Run(async () =>
    {
        var task = _menus.PrepareAsync(IntPtr.Zero, new[] { _dir.Combine("gone.txt") }, background: false, extended: false);

        await Assert.ThrowsAnyAsync<Exception>(() => task);
        _menus.Release(task);
        await _menus.PrewarmAsync(null, _dir.Path);   // queued behind the release: the thread is still working
    });

    [Fact]
    public void Showing_a_menu_that_failed_to_build_reports_the_failure() => WpfThread.Run(async () =>
    {
        var task = _menus.PrepareAsync(IntPtr.Zero, new[] { _dir.Combine("gone.txt") }, background: false, extended: false);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _menus.ShowAsync(task, IntPtr.Zero, new Point(0, 0), null));
    });

    [Fact]
    public void A_menu_with_no_items_is_refused() => WpfThread.Run(async () =>
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _menus.PrepareAsync(IntPtr.Zero, Array.Empty<string>(), background: false, extended: false));
    });

    [Fact]
    public void A_menu_is_not_shown_once_the_window_it_was_opened_from_is_no_longer_active() => WpfThread.Run(async () =>
    {
        // A window that is never shown can't be the foreground window: the menu is dropped instead of
        // popping up over whatever the user went on to.
        var window = new Window { ShowActivated = false };
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var task = _menus.PrepareAsync(handle, new[] { _dir.File("late.txt") }, background: false, extended: false);
        var menu = await task;

        var shown = _menus.ShowAsync(task, handle, new Point(0, 0), null);
        Assert.Same(shown, await Task.WhenAny(shown, Task.Delay(5000)));   // returned without waiting for a pick
        await shown;
        await Wait.Until(() => menu.HMenu == IntPtr.Zero, "the dropped menu to be freed");
        window.Close();
    });

    [Fact]
    public void Requests_run_in_order_so_a_release_never_overtakes_its_build() => WpfThread.Run(async () =>
    {
        string file = _dir.File("order.txt");
        var first = _menus.PrepareAsync(IntPtr.Zero, new[] { file }, background: false, extended: false);
        _menus.Release(first);   // before it is built: freed once it is

        var menu = await first;
        await Wait.Until(() => menu.HMenu == IntPtr.Zero, "the released menu to be freed");
    });
}
