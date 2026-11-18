using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PreparedMenu = FileStar.Platform.ShellContextMenu.PreparedMenu;

namespace FileStar.Platform;

/// <summary>
/// Builds, shows and runs Windows Explorer's right-click menus on a thread of their own. Building one makes
/// every installed shell extension add its items (0.4–2 s here); on the UI thread that froze FileStar's own
/// menu, which builds the Explorer menu ahead of time while it is open. A menu's COM objects belong to the
/// thread that built them, so the whole life of a menu stays on this thread.
/// </summary>
public sealed class ShellMenuThread : IDisposable
{
    readonly Dispatcher _dispatcher;
    readonly HwndSource _owner;   // hidden window of this thread: owns the popup and receives its messages
    readonly uint _threadId;

    // A shown menu runs its own message loop, which would also run this thread's queued work: a second menu
    // inside the first, or a build freezing the open menu. Work that arrives meanwhile waits until it closes.
    bool _tracking;
    readonly List<(DispatcherPriority Priority, Action Step)> _afterMenu = new();

    public ShellMenuThread()
    {
        var ready = new TaskCompletionSource<(Dispatcher, HwndSource, uint)>();
        var thread = new Thread(() =>
        {
            try
            {
                OleInitialize(IntPtr.Zero);   // the shell's 복사/잘라내기 put files on the OLE clipboard from this thread
                var dispatcher = Dispatcher.CurrentDispatcher;
                // A shell extension failing inside a menu message must not take the process down.
                dispatcher.UnhandledException += (_, e) =>
                {
                    Perf.Log("shell menu thread: " + e.Exception);
                    e.Handled = true;
                };
                var owner = new HwndSource(new HwndSourceParameters("FileStar shell menu")
                {
                    WindowStyle = unchecked((int)WS_POPUP),
                    ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                    Width = 0,
                    Height = 0,
                });
                ready.SetResult((dispatcher, owner, GetCurrentThreadId()));
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Shell menus",
        };
        thread.SetApartmentState(ApartmentState.STA);   // shell extensions are apartment-threaded COM objects
        thread.Start();
        (_dispatcher, _owner, _threadId) = ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Builds and discards a menu once, so the shell extensions are loaded and initialized on this thread before
    /// the user's first menu.
    /// </summary>
    public Task PrewarmAsync(string? samplePath, string folderPath) =>
        Run(() => ShellContextMenu.Prewarm(samplePath, folderPath), DispatcherPriority.Background);

    /// <summary>Builds the menu for <paramref name="paths"/>, or for the background of the folder <c>paths[0]</c>.</summary>
    /// <param name="window">The window shell extensions treat as the menu's owner.</param>
    public Task<PreparedMenu> PrepareAsync(IntPtr window, IReadOnlyList<string> paths, bool background, bool extended) =>
        Run(() => ShellContextMenu.Prepare(window, paths, background, extended));

    /// <summary>Frees a menu that will not be shown, once it is built.</summary>
    public void Release(Task<PreparedMenu> menu) =>
        // Queued behind the build, so it runs once the menu exists (or the build failed and freed it).
        _ = Run(() =>
        {
            if (menu.IsCompletedSuccessfully)
                menu.Result.Dispose();
            else if (menu.Exception is { } failed)   // reading it also marks it observed
                Perf.Log("unused shell menu failed to build: " + failed.InnerException?.GetType().Name);
        });

    /// <summary>
    /// Shows <paramref name="menu"/> at <paramref name="screenPoint"/> and runs the picked command, unless
    /// <paramref name="handleVerb"/> (called on the calling thread) handles its verb. Frees the menu afterwards.
    /// Nothing is shown if <paramref name="window"/> is no longer the active window by the time the menu is built.
    /// </summary>
    /// <param name="window">The window the menu was opened from; the command's dialogs belong to it.</param>
    public async Task ShowAsync(Task<PreparedMenu> menu, IntPtr window, Point screenPoint, ShellContextMenu.VerbHandler? handleVerb)
    {
        try
        {
            var prepared = await menu;
            uint uiThread = GetCurrentThreadId();
            var choice = await Run(() => Track(prepared, screenPoint, uiThread, window));
            if (choice is not { } picked)
                return;
            if (picked.Verb.Length > 0 && handleVerb?.Invoke(picked.Verb, prepared.Paths) == true)
                return;
            await Run(() => ShellContextMenu.Invoke(prepared, picked.Offset, window, screenPoint, picked.Shift, picked.Control));
        }
        finally
        {
            Release(menu);
        }
    }

    ShellContextMenu.Choice? Track(PreparedMenu prepared, Point screenPoint, uint uiThread, IntPtr window)
    {
        // Built on demand, the menu may be ready only after the user has moved on to another program.
        if (GetAncestor(GetForegroundWindow(), GA_ROOTOWNER) != GetAncestor(window, GA_ROOTOWNER))
            return null;
        // Sharing the UI thread's input state makes this thread's menu behave as one opened by the active
        // window: a click anywhere else closes it. Keys go to the thread whose window has the focus, so the
        // hidden owner takes it for the menu's arrow keys, Esc and access keys, and gives it back afterwards.
        bool attached = AttachThreadInput(_threadId, uiThread, true);
        if (attached)
            SetFocus(_owner.Handle);
        _tracking = true;
        try
        {
            return ShellContextMenu.Track(prepared, _owner, screenPoint);
        }
        finally
        {
            _tracking = false;
            if (attached)
            {
                if (GetFocus() == _owner.Handle)
                    SetFocus(window);
                AttachThreadInput(_threadId, uiThread, false);
            }
            foreach (var (priority, step) in _afterMenu)
                _dispatcher.BeginInvoke(priority, step);
            _afterMenu.Clear();
        }
    }

    Task Run(Action work, DispatcherPriority priority = DispatcherPriority.Normal) =>
        Run<object?>(() =>
        {
            work();
            return null;
        }, priority);

    Task<T> Run<T>(Func<T> work, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Step()
        {
            if (_tracking)
            {
                _afterMenu.Add((priority, Step));
                return;
            }
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
        _dispatcher.BeginInvoke(priority, (Action)Step);
        return done.Task;
    }

    /// <summary>The managed ID of this thread (for tests).</summary>
    internal Task<int> ThreadIdAsync() => Run(() => Environment.CurrentManagedThreadId);

    public void Dispose()
    {
        if (_dispatcher.HasShutdownStarted)
            return;
        // Not waited for: a shell extension stuck in a command must not hold up closing the window.
        _dispatcher.BeginInvoke(_owner.Dispose);
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    const uint WS_POPUP = 0x80000000;
    const int WS_EX_TOOLWINDOW = 0x80;
    const uint GA_ROOTOWNER = 3;

    [DllImport("ole32.dll")]
    static extern int OleInitialize(IntPtr reserved);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
