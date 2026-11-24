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

    /// <summary>The first <paramref name="maxBytes"/> bytes of <paramref name="format"/> in the clipboard, or null if it isn't there.</summary>
    public static byte[]? ReadFromClipboard(string format, int maxBytes) => Use(data => Read(data, format, maxBytes));

    /// <summary>The first <paramref name="maxBytes"/> bytes of <paramref name="format"/>'s global memory, or null if it isn't offered that way.</summary>
    public static byte[]? Read(IComDataObject data, string format, int maxBytes)
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
        catch (COMException)
        {
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
