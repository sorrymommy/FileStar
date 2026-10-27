using System.IO;

namespace FileStar.Services;

/// <summary>
/// What a location string means: a real folder, the virtual 내 PC, or a search / tag result list (see
/// <see cref="SearchLocation"/>), and how each is named on screen. No state beyond a cache of drive labels.
/// </summary>
public static class Locations
{
    static readonly Dictionary<string, string> RootLabels = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The canonical form a tab stores: 내 PC, a result list as it is, or a normalized folder path.</summary>
    public static string Canonical(string path) =>
        IsThisPc(path) ? ThisPc : IsVirtual(path) ? path : Normalize(path);

    public static bool IsRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return root != null && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> itself or somewhere inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        string root = folder.TrimEnd('\\');
        return string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>If <paramref name="path"/> is directly inside <paramref name="folder"/>, its name (to reselect it).</summary>
    public static string? ChildNameOf(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : null;

    /// <summary>All of <paramref name="paths"/> sit directly in <paramref name="folder"/>.</summary>
    public static bool AllDirectlyIn(IEnumerable<string> paths, string folder) =>
        paths.All(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

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

    /// <summary>What the address bar shows: the full path of a folder, the name of anything virtual.</summary>
    public static string AddressText(string path) => IsVirtual(path) ? DisplayName(path) : path;

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

    public static string DriveTypeName(DriveType type) => type switch
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

    /// <summary>"C: 여유 공간 …" for the status bar, or "" where it doesn't apply.</summary>
    public static string DriveSpaceText(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return drive.IsReady
                ? $"{drive.Name.TrimEnd('\\')} 여유 공간 {SizeFormat.Bytes(drive.AvailableFreeSpace)} / {SizeFormat.Bytes(drive.TotalSize)}"
                : "";
        }
        catch
        {
            return "";
        }
    }
}

public static class SizeFormat
{
    public static string Bytes(long bytes)
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
    public static string KB(long bytes) => $"{(bytes + 1023) / 1024:N0} KB";
}
