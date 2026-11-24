using System.IO;
using System.Text;
using System.Windows;
using FileStar.Platform;
using FileStar.Tests.Support;

namespace FileStar.Tests.Platform;

/// <summary>
/// Files copied on a Remote Desktop host reach the clipboard as a file descriptor list plus "FileContents" streams,
/// not as paths (issue #151). These tests hand such data straight to a folder, without touching the user's clipboard.
/// </summary>
public sealed class VirtualFilesTests : IDisposable
{
    const int RecordSize = 592, FD_FILESIZE = 0x40;
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A FILEGROUPDESCRIPTORW for files of the given names and sizes.</summary>
    static byte[] Descriptor(params (string Name, int Size)[] files)
    {
        var bytes = new byte[4 + files.Length * RecordSize];
        BitConverter.GetBytes((uint)files.Length).CopyTo(bytes, 0);
        for (int i = 0; i < files.Length; i++)
        {
            int record = 4 + i * RecordSize;
            BitConverter.GetBytes(FD_FILESIZE).CopyTo(bytes, record);
            BitConverter.GetBytes(files[i].Size).CopyTo(bytes, record + 68);
            Encoding.Unicode.GetBytes(files[i].Name).CopyTo(bytes, record + 72);
        }
        return bytes;
    }

    static DataObject VirtualFile(string name, byte[] content)
    {
        var data = new DataObject();
        data.SetData(VirtualFiles.DescriptorFormat, new MemoryStream(Descriptor((name, content.Length))));
        data.SetData("FileContents", new MemoryStream(content));
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));   // copy
        return data;
    }

    [Fact]
    public void Top_level_names_skip_folder_contents_and_unsafe_names()
    {
        var names = VirtualFiles.TopLevelNames(Descriptor(
            ("보고서.docx", 1), ("사진", 0), (@"사진\a.jpg", 1), ("보고서.DOCX", 1),
            (@"..\밖.txt", 1), ("", 0), ("a:b", 1), ("...", 0), (@"...\x.txt", 1), ("끝공백 ", 1),
            (@"C:\Windows", 0), (@"\\server\share", 0), ("CON", 1), ("nul.txt", 1), ("com1 .log", 1)));

        Assert.Equal(new[] { "보고서.docx", "사진" }, names);
    }

    [Fact]
    public void A_count_larger_than_the_data_reads_only_what_is_there()
    {
        var bytes = Descriptor(("a.txt", 1));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 0);

        Assert.Equal(new[] { "a.txt" }, VirtualFiles.TopLevelNames(bytes));
        Assert.Empty(VirtualFiles.TopLevelNames(new byte[] { 1, 0 }));
        Assert.Empty(VirtualFiles.TopLevelNames(bytes.AsSpan(0, 100)));
    }

    [Fact]
    public void A_format_is_read_as_raw_bytes_up_to_the_limit()
    {
        var data = new DataObject();
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2)));

        Assert.Equal(BitConverter.GetBytes(2), ClipboardData.Read(data, "Preferred DropEffect", 4));
        Assert.Equal(2, ClipboardData.Read(data, "Preferred DropEffect", 2)!.Length);
        Assert.Null(ClipboardData.Read(data, VirtualFiles.DescriptorFormat, 4));
    }

    [Fact]
    public void Pasting_virtual_files_writes_them_out_and_reports_them_for_undo() => WpfThread.Run(async () =>
    {
        byte[] content = Encoding.UTF8.GetBytes("원격 데스크톱에서 복사한 내용");
        string folder = _dir.Folder("target");

        var result = VirtualFiles.Paste(IntPtr.Zero, folder, VirtualFile("원격.txt", content));

        string pasted = Path.Combine(folder, "원격.txt");
        Assert.True(result.Dropped);
        Assert.Equal(new[] { pasted }, result.Incoming);
        // The shell copies in the background; wait until it has written and let go of the file.
        await Wait.Until(() => ReadsBack(pasted, content), "the pasted file");
    });

    static bool ReadsBack(string path, byte[] expected)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected); }
        catch (IOException) { return false; }
    }
}
