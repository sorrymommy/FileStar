using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FileStar.Platform;

/// <summary>Tells a window whenever the clipboard changes, in any program (WM_CLIPBOARDUPDATE).</summary>
public static class ClipboardWatcher
{
    const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>
    /// Call once the window has a handle (SourceInitialized); Windows drops the listener when the window goes.
    /// <paramref name="changed"/> runs after the message, not inside it: reading the clipboard while its owner is
    /// still announcing the change could wait on that owner. A burst of changes runs it once.
    /// </summary>
    public static void Watch(Window window, Action changed)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source == null || !AddClipboardFormatListener(source.Handle))
            return;
        bool queued = false;
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_CLIPBOARDUPDATE && !queued)
            {
                queued = true;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    queued = false;
                    changed();
                });
            }
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
