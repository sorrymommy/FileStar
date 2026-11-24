using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace FileStar.Platform;

/// <summary>
/// Files offered as contents rather than paths (a file descriptor list plus "FileContents" streams): what Remote
/// Desktop puts on the clipboard for files copied on the other computer, and what zip folders, phones and mail
/// attachments offer. There is no path to copy from, so they are pasted the way Explorer pastes them: dropped on
/// the folder's own shell drop target, which writes them out with Windows' progress and conflict dialogs.
/// </summary>
public static class VirtualFiles
{
    public const string DescriptorFormat = "FileGroupDescriptorW";

    /// <summary>Clipboard formats that announce virtual files (the ANSI one is still used by older programs).</summary>
    public static readonly IReadOnlyList<string> Formats = [DescriptorFormat, "FileGroupDescriptor"];

    // FILEGROUPDESCRIPTORW: a UINT count, then FILEDESCRIPTORW records of 592 bytes whose cFileName (260 WCHARs) starts at byte 72.
    const int CountSize = 4, RecordSize = 592, NameOffset = 72, NameBytes = 260 * 2;
    // Names only serve Ctrl+Z, so a huge list is read in part rather than stalling the UI thread.
    const int MaxRecords = 10_000, MaxDescriptorBytes = CountSize + MaxRecords * RecordSize;
    const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, MK_LBUTTON = 1;

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Whether a paste was accepted, and the top-level paths it may create: named in the data and absent before the
    /// paste. The shell copies in the background, so they may not exist yet (or ever, if the user skipped them).
    /// </summary>
    public sealed record PasteResult(bool Dropped, IReadOnlyList<string> Incoming);

    /// <summary>
    /// Top-level item names in a FILEGROUPDESCRIPTORW (a folder's contents are listed too, as "folder\file").
    /// The data comes from another program, so the count is bounded by the data and anything that isn't a plain
    /// name of an item directly in a folder is skipped.
    /// </summary>
    public static IReadOnlyList<string> TopLevelNames(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < CountSize)
            return [];
        long count = Math.Min(Math.Min(BitConverter.ToUInt32(descriptor[..CountSize]), (descriptor.Length - CountSize) / RecordSize), MaxRecords);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.Unicode.GetString(descriptor.Slice(CountSize + i * RecordSize + NameOffset, NameBytes));
            int end = name.IndexOf('\0');
            string top = (end >= 0 ? name[..end] : name).Split('\\', '/')[0];
            if (IsPlainName(top) && seen.Add(top))
                names.Add(top);
        }
        return names;
    }

    // Windows drops trailing dots and spaces ("..." would mean the folder itself) and maps device names to devices.
    static bool IsPlainName(string name) =>
        name.Length > 0 && !name.EndsWith('.') && !name.EndsWith(' ') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !DeviceNames.Contains(name.Split('.')[0].TrimEnd());

    /// <summary>Pastes what is on the clipboard into <paramref name="folder"/>, as Explorer's Ctrl+V does. Call on an STA thread.</summary>
    public static PasteResult PasteClipboard(IntPtr owner, string folder) => ClipboardData.Use(data => Paste(owner, folder, data));

    /// <summary>
    /// Drops <paramref name="data"/> on <paramref name="folder"/>, reading the names it brings from that same data
    /// object (so a clipboard change in between can't mix them up). The names come only from the Unicode descriptor.
    /// </summary>
    public static PasteResult Paste(IntPtr owner, string folder, IComDataObject data)
    {
        var names = ClipboardData.Read(data, DescriptorFormat, MaxDescriptorBytes) is { } descriptor ? TopLevelNames(descriptor) : [];
        var incoming = names.Select(n => Path.Combine(folder, n)).Where(p => IsDirectlyIn(p, folder) && !Exists(p)).ToList();
        bool dropped = Drop(owner, folder, data);
        return new PasteResult(dropped, dropped ? incoming : []);
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    static bool IsDirectlyIn(string path, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops <paramref name="data"/> on <paramref name="folder"/>. The folder chooses copy or move from the data's
    /// "Preferred DropEffect", as for a paste in Explorer.
    /// </summary>
    static bool Drop(IntPtr owner, string folder, IComDataObject data)
    {
        var target = (IDropTarget)ShellContextMenu.UIObjectOf(owner, [folder], typeof(IDropTarget).GUID);
        bool entered = false;
        try
        {
            int effect = DROPEFFECT_COPY | DROPEFFECT_MOVE;
            var point = new POINTL();
            Marshal.ThrowExceptionForHR(target.DragEnter(data, MK_LBUTTON, point, ref effect));
            entered = effect != DROPEFFECT_NONE;
            if (!entered)
                return false;
            int result = target.Drop(data, MK_LBUTTON, point, ref effect);
            entered = false;   // Drop ends the drag, even when it fails
            Marshal.ThrowExceptionForHR(result);
            return effect != DROPEFFECT_NONE;
        }
        finally
        {
            if (entered)
                target.DragLeave();
            Marshal.FinalReleaseComObject(target);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTL
    {
        public int X, Y;
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter(IComDataObject data, int keyState, POINTL point, ref int effect);
        [PreserveSig] int DragOver(int keyState, POINTL point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IComDataObject data, int keyState, POINTL point, ref int effect);
    }
}
