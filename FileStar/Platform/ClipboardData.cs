using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace FileStar.Platform;

/// <summary>
/// Raw access to clipboard formats written by other programs. WPF's <c>Clipboard.GetData</c> deserializes a
/// non-standard format with BinaryFormatter when its bytes carry WPF's serialized-object marker, so anything another
/// process (or a Remote Desktop server, through clipboard redirection) put there is read here as plain bytes instead.
/// </summary>
public static class ClipboardData
{
    /// <summary>Runs <paramref name="use"/> on the clipboard's current data object. Call on an STA thread.</summary>
    public static T Use<T>(Func<IComDataObject, T> use)
    {
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var data));
        try
        {
            return use(data);
        }
        finally
        {
            Marshal.ReleaseComObject(data);
        }
    }

    const uint CF_HDROP = 15;

    /// <summary>Files (a file drop list) are on the clipboard. Doesn't open the clipboard, so it never waits on another program.</summary>
    public static bool HasFiles() => IsClipboardFormatAvailable(CF_HDROP);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsClipboardFormatAvailable(uint format);

    const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);
    const int BusyRetries = 5, BusyRetryDelayMs = 20;

    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of <paramref name="format"/> in the clipboard, or null if it isn't there.
    /// Waits briefly while another program has the clipboard open (clipboard history and other watchers read it the
    /// moment it changes, just when FileStar does), as WPF's own Clipboard calls do; throws if it stays busy, rather
    /// than answering as if the format weren't there.
    /// </summary>
    public static byte[]? ReadFromClipboard(string format, int maxBytes)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return Use(data => Read(data, format, maxBytes, throwIfBusy: true));
            }
            catch (COMException ex) when (ex.HResult == CLIPBRD_E_CANT_OPEN && attempt < BusyRetries)
            {
                Thread.Sleep(BusyRetryDelayMs);
            }
        }
    }

    /// <summary>The first <paramref name="maxBytes"/> bytes of <paramref name="format"/>'s global memory, or null if it isn't offered that way.</summary>
    public static byte[]? Read(IComDataObject data, string format, int maxBytes) => Read(data, format, maxBytes, throwIfBusy: false);

    static byte[]? Read(IComDataObject data, string format, int maxBytes, bool throwIfBusy)
    {
        var request = new FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat(format)),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        if (request.cfFormat == 0 || data.QueryGetData(ref request) != 0)
            return null;
        STGMEDIUM medium;
        try
        {
            data.GetData(ref request, out medium);
        }
        catch (COMException ex) when (!(throwIfBusy && ex.HResult == CLIPBRD_E_CANT_OPEN))
        {
            Perf.Log($"clipboard {format}: 0x{ex.HResult:X8} {ex.Message}");
            return null;   // offered, but the owner failed to render it
        }
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
                return null;
            IntPtr memory = GlobalLock(medium.unionmember);
            if (memory == IntPtr.Zero)
                return null;
            try
            {
                var bytes = new byte[(int)Math.Min((ulong)GlobalSize(medium.unionmember), (ulong)maxBytes)];
                Marshal.Copy(memory, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    [DllImport("ole32.dll")]
    static extern int OleGetClipboard(out IComDataObject data);

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern UIntPtr GlobalSize(IntPtr memory);
}
