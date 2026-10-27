using System.IO;

namespace FileStar.ViewModels;

/// <summary>What "내 PC" lists, as in Explorer: the user's main folders, then every drive.</summary>
public static class ThisPcItems
{
    public static List<FileItem> Build()
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
            items.Add(DriveItem(drive, order++));
        return items;
    }

    static FileItem DriveItem(DriveInfo drive, int order)
    {
        string type = Locations.DriveTypeName(drive.DriveType);
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
        return new FileItem
        {
            Name = Locations.DriveLabel(drive),
            FullPath = drive.RootDirectory.FullName,
            IsDirectory = true,
            SortGroup = 1,
            SortOrder = order,
            ExactIcon = true,
            TypeOverride = free is long f && total is long t
                ? $"{type} · {SizeFormat.Bytes(f)} 사용 가능 / 전체 {SizeFormat.Bytes(t)}"
                : type,
        };
    }

    /// <summary>The 이동 menu's shortcuts (내 PC, 바탕 화면, 문서 …) by their menu tag.</summary>
    public static string? SpecialFolder(string? key)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return key switch
        {
            "ThisPc" => Locations.ThisPc,
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads" => Path.Combine(home, "Downloads"),
            "Home" => home,
            _ => null,
        };
    }
}
