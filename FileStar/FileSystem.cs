using System.IO;

namespace FileStar;

internal static class FileSystem
{
    static readonly Dictionary<string, string> RootLabels = new(StringComparer.OrdinalIgnoreCase);

    public static bool ShowHidden { get; set; }

    public static EnumerationOptions Options => new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
    };

    public static List<FileSystemInfo> GetEntries(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options).ToList();

    public static List<DirectoryInfo> GetSubfolders(string path)
    {
        try
        {
            var list = new DirectoryInfo(path).EnumerateDirectories("*", Options).ToList();
            list.Sort((a, b) => Native.StrCmpLogicalW(a.Name, b.Name));
            return list;
        }
        catch
        {
            return new();
        }
    }

    public static bool HasSubfolders(string path)
    {
        try { return Directory.EnumerateDirectories(path, "*", Options).Any(); }
        catch { return false; }
    }

    /// <summary>The virtual "내 PC" location (user folders and drives). Not a real path, so it never collides with one.</summary>
    public const string ThisPc = "내 PC";

    /// <summary>Also accepts what people type for it in the address bar.</summary>
    public static bool IsThisPc(string? path) =>
        path != null && (path.Trim() is var p) &&
        (p.Equals(ThisPc, StringComparison.OrdinalIgnoreCase) || p.Equals("내PC", StringComparison.OrdinalIgnoreCase) ||
         p.Equals("내 컴퓨터", StringComparison.OrdinalIgnoreCase) || p.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
         p.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Any location that isn't a real folder: 내 PC, or a search / tag result list.</summary>
    public static bool IsVirtual(string? path) => IsThisPc(path) || SearchLocation.TryParse(path) != null;

    /// <summary>What "내 PC" lists, as in Explorer: the user's main folders, then every drive.</summary>
    public static List<FileItem> GetThisPcItems()
    {
        var items = new List<FileItem>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string Name, string Path)[] folders =
        {
            ("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("다운로드", Path.Combine(home, "Downloads")),
            ("사진", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("음악", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("동영상", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        int order = 0;
        foreach (var (name, path) in folders)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                continue;
            var item = FileItem.From(new DirectoryInfo(path));
            if (item != null)
                items.Add(item.ForThisPc(name, group: 0, order: order++, typeName: "폴더"));
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            string type = DriveTypeName(drive.DriveType);
            long? total = null, free = null;
            try
            {
                if (drive.IsReady)
                {
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
            }
            catch { }
            items.Add(new FileItem
            {
                Name = DriveLabel(drive),
                FullPath = drive.RootDirectory.FullName,
                IsDirectory = true,
                SortGroup = 1,
                SortOrder = order++,
                ExactIcon = true,
                TypeOverride = free is long f && total is long t
                    ? $"{type} · {FormatBytes(f)} 사용 가능 / 전체 {FormatBytes(t)}"
                    : type,
            });
        }
        return items;
    }

    public static bool IsRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root != null && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"새 폴더", or "새 폴더 (2)" … if that name is taken in <paramref name="folder"/>.</summary>
    public static string NewFolderName(string folder)
    {
        string name = "새 폴더";
        for (int n = 2; Directory.Exists(Path.Combine(folder, name)) || File.Exists(Path.Combine(folder, name)); n++)
            name = $"새 폴더 ({n})";
        return name;
    }

    /// <summary>"Windows (C:)" for drive roots, the folder name otherwise.</summary>
    public static string DisplayName(string path)
    {
        if (IsThisPc(path))
            return ThisPc;
        if (SearchLocation.TryParse(path) is { } search)
            return search.Title;
        if (IsRoot(path))
            return RootLabel(path);
        string name = Path.GetFileName(path.TrimEnd('\\'));
        return name.Length > 0 ? name : path;
    }

    static string RootLabel(string root)
    {
        if (root.StartsWith(@"\\"))
            return root.TrimEnd('\\');
        if (RootLabels.TryGetValue(root, out var label))
            return label;
        try { label = DriveLabel(new DriveInfo(root)); }
        catch { label = root; }
        return RootLabels[root] = label;
    }

    public static string DriveLabel(DriveInfo drive)
    {
        string label = "";
        try
        {
            if (drive.IsReady)
                label = drive.VolumeLabel;
        }
        catch { }
        if (string.IsNullOrEmpty(label))
            label = DriveTypeName(drive.DriveType);
        return $"{label} ({drive.Name.TrimEnd('\\')})";
    }

    static string DriveTypeName(DriveType type) => type switch
    {
        DriveType.Fixed => "로컬 디스크",
        DriveType.Network => "네트워크 드라이브",
        DriveType.Removable => "이동식 디스크",
        DriveType.CDRom => "CD 드라이브",
        _ => "드라이브",
    };

    public static string Normalize(string path)
    {
        if (path.Length == 2 && path[1] == ':')
            path += "\\";
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\') : full;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "바이트", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} 바이트" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Explorer-style size: always KB, rounded up.</summary>
    public static string FormatKB(long bytes) => $"{(bytes + 1023) / 1024:N0} KB";
}
