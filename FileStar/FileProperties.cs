using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileStar;

/// <summary>File details beyond what FileInfo offers: 8.3 name, size on disk, all attribute bits, shell properties.</summary>
internal static class FileProperties
{
    /// <summary>Attribute bits shown as checkboxes, in display order (3 per row).</summary>
    public static readonly (string Label, uint Flag)[] Attributes =
    {
        ("읽기 전용", 0x1), ("숨김", 0x2), ("보관", 0x20),
        ("시스템", 0x4), ("디렉터리", 0x10), ("일반", 0x80),
        ("임시", 0x100), ("정션/링크", 0x400), ("압축", 0x800),
        ("오프라인", 0x1000), ("색인 안 함", 0x2000), ("암호화", 0x4000),
        ("항상 이 장치에 유지", 0x80000), ("고정 해제", 0x100000), ("열 때 다운로드", 0x400000),
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetFileAttributes(string fileName);

    /// <summary>Raw attribute bits, including cloud bits (pinned, recall…) that <see cref="FileAttributes"/> lacks.</summary>
    public static uint RawAttributes(string path) => GetFileAttributes(path) is var a && a != 0xFFFFFFFF ? a : 0;

    /// <summary>The 8.3 file name (e.g. "AIM시~1.XLS"), or the normal name when 8.3 names are disabled.</summary>
    public static string ShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        return GetShortPathName(path, buffer, buffer.Capacity) > 0 ? Path.GetFileName(buffer.ToString()) : Path.GetFileName(path);
    }

    /// <summary>Allocated size and cluster count; cloud-only files occupy 0 bytes.</summary>
    public static (long Bytes, long Clusters)? SizeOnDisk(string path)
    {
        try
        {
            uint low = GetCompressedFileSize(path, out uint high);
            if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
                return null;
            long size = ((long)high << 32) | low;
            string? root = Path.GetPathRoot(path);
            if (root == null || !GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _))
                return (size, 0);
            long cluster = (long)spc * bps;
            long clusters = (size + cluster - 1) / cluster;
            return (clusters * cluster, clusters);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Total size and file count of a folder tree (skips folders it can't read).</summary>
    public static (long Bytes, int Files, int Folders) FolderSize(string path, CancellationToken token)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            if (info is FileInfo file)
            {
                bytes += file.Length;
                files++;
            }
            else
                folders++;
        }
        return (bytes, files, folders);
    }

    // Shell columns already shown in the fixed part of the panel (by header name, Korean and English).
    static readonly HashSet<string> SkippedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "이름", "크기", "항목 유형", "수정한 날짜", "만든 날짜", "액세스한 날짜", "특성", "폴더 이름", "폴더 경로", "폴더",
        "경로", "파일 위치", "파일 이름", "파일 확장명", "유형", "인식된 유형", "공유", "오프라인 상태", "사용 가능 여부",
        "Name", "Size", "Item type", "Date modified", "Date created", "Date accessed", "Attributes", "Folder name",
        "Folder path", "Folder", "Path", "File location", "Filename", "File extension", "Type", "Perceived type",
        // Drive/computer-level values that say nothing about the file itself
        "전체 크기", "사용 가능한 공간", "사용된 공간", "컴퓨터", "공유됨", "공유 상태", "연결 상태", "가용성 상태", "인식 유형",
        "Total size", "Space free", "Space used", "Computer", "Shared", "Sharing status", "Link status", "Availability status",
    };

    static readonly Dictionary<string, string[]> HeaderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every non-empty Windows shell property of the item (author, title, page count, dimensions…),
    /// the same values Explorer's "자세히" columns offer. Must run on an STA thread.
    /// </summary>
    public static List<(string Name, string Value)> ShellDetails(string path)
    {
        var result = new List<(string, string)>();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir == null)
                return result;
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return result;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? folder = shell.NameSpace(dir);
            if (folder == null)
                return result;
            dynamic? item = folder.ParseName(Path.GetFileName(path));
            if (item == null)
                return result;

            if (!HeaderCache.TryGetValue(dir, out var headers))
            {
                headers = new string[360];
                for (int i = 0; i < headers.Length; i++)
                    headers[i] = (string)(folder.GetDetailsOf(null, i) ?? "");
                HeaderCache[dir] = headers;
            }
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].Length == 0 || SkippedColumns.Contains(headers[i]))
                    continue;
                string value = Clean((string)(folder.GetDetailsOf(item, i) ?? ""));
                if (value.Length > 0)
                    result.Add((headers[i], value));
            }
        }
        catch { }
        return result;
    }

    // Shell values carry invisible direction marks (U+200E/U+200F) around dates and numbers.
    static string Clean(string value) => value.Replace("‎", "").Replace("‏", "").Trim();
}
