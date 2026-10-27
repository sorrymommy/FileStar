using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FileStar;

/// <summary>
/// Reads the preview that Hancom Office embeds in every document — a first-page image (PrvImage) and the body
/// text (PrvText) — so .hwp/.hwpx can be previewed without Hancom's shell extension.
/// </summary>
internal static class HancomPreview
{
    public static bool Supports(string extension) =>
        extension.Equals(".hwp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase);

    public static (byte[]? Image, string? Text) Read(string path)
    {
        var (image, text) = Path.GetExtension(path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ? ReadHwpx(path) : ReadHwp(path);
        return (image, text == null ? null : CleanTableMarkup(text));
    }

    /// <summary>PrvText writes table rows as "&lt;cell&gt;&lt;cell&gt;…"; show them as "cell │ cell" instead.</summary>
    static string CleanTableMarkup(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            if (!line.Contains('<'))
                return line;
            var cells = System.Text.RegularExpressions.Regex.Matches(line, "<([^<>]*)>")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(c => c.Length > 0)
                .ToList();
            return cells.Count > 0 ? string.Join(" │ ", cells) : null;
        });
        return string.Join(Environment.NewLine, lines.Where(l => l != null));
    }

    /// <summary>HWPX is a zip: Preview/PrvImage.png and Preview/PrvText.txt (UTF-8).</summary>
    static (byte[]?, string?) ReadHwpx(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        byte[]? image = null;
        string? text = null;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("Preview/PrvImage", StringComparison.OrdinalIgnoreCase))
                image = ReadAll(entry.Open());
            else if (entry.FullName.StartsWith("Preview/PrvText", StringComparison.OrdinalIgnoreCase))
                text = Encoding.UTF8.GetString(ReadAll(entry.Open()));
        }
        return (image, text);
    }

    static byte[] ReadAll(Stream source)
    {
        using (source)
        using (var memory = new MemoryStream())
        {
            source.CopyTo(memory);
            return memory.ToArray();
        }
    }

    /// <summary>HWP 5.x is an OLE compound file with "PrvImage" and "PrvText" (UTF-16) streams.</summary>
    static (byte[]?, string?) ReadHwp(string path)
    {
        var iid = typeof(IStorage).GUID;
        // Transacted + deny-none lets us read a document that Hancom currently has open.
        int hr = StgOpenStorageEx(path, STGM_READ | STGM_SHARE_DENY_NONE | STGM_TRANSACTED, 0, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out var storage);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var image = ReadStream(storage, "PrvImage");
            var textBytes = ReadStream(storage, "PrvText");
            return (image, textBytes == null ? null : Encoding.Unicode.GetString(textBytes));
        }
        finally
        {
            Marshal.FinalReleaseComObject(storage);
        }
    }

    static byte[]? ReadStream(IStorage storage, string name)
    {
        IStream stream;
        try
        {
            storage.OpenStream(name, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out stream);
        }
        catch (COMException)
        {
            return null;   // stream not present
        }
        try
        {
            stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
            var buffer = new byte[stat.cbSize];
            IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                stream.Read(buffer, buffer.Length, readPtr);
                int read = Marshal.ReadInt32(readPtr);
                return read == buffer.Length ? buffer : buffer[..read];
            }
            finally
            {
                Marshal.FreeHGlobal(readPtr);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(stream);
        }
    }

    const uint STGM_READ = 0x0, STGM_SHARE_EXCLUSIVE = 0x10, STGM_SHARE_DENY_NONE = 0x40, STGM_TRANSACTED = 0x10000;

    // Only OpenStream is called; the members before it must still be declared so the vtable lines up.
    [ComImport, Guid("0000000b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int StgOpenStorageEx(string name, uint mode, uint format, uint attributes, IntPtr options,
        IntPtr reserved, ref Guid riid, out IStorage storage);
}
