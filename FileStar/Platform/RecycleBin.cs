using System.IO;
using System.Security.Principal;
using System.Text;

namespace FileStar.Platform;

/// <summary>Puts a recycled item back where it was, the way the Recycle Bin's own 복원 does.</summary>
public static class RecycleBin
{
    /// <summary>
    /// Each recycled item is a pair in X:\$Recycle.Bin\{user SID}: "$R…" (the item itself) and "$I…" (its
    /// original path and deletion time). Restoring moves $R back and removes $I.
    /// </summary>
    public static bool Restore(string originalPath, DateTime from, DateTime to)
    {
        try
        {
            string? root = Path.GetPathRoot(originalPath);
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (root == null || sid == null)
                return false;
            string bin = Path.Combine(root, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin))
                return false;
            // The deletion made by that operation (the same path may have been recycled again since).
            var match = Directory.EnumerateFiles(bin, "$I*")
                .Select(info => (Info: info, Record: ReadInfo(info)))
                .Where(x => x.Record is { } r && string.Equals(r.Path, originalPath, StringComparison.OrdinalIgnoreCase) &&
                            r.Deleted >= from.AddSeconds(-2) && r.Deleted <= to.AddSeconds(2))
                .OrderBy(x => x.Record!.Value.Deleted)   // that operation's deletion came before any later one
                .FirstOrDefault();
            if (match.Info == null)
                return false;
            string item = Path.Combine(bin, "$R" + Path.GetFileName(match.Info)[2..]);
            if (DiskPaths.Exists(originalPath))
                return false;   // something new took its place; don't overwrite it
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            if (!DiskPaths.Exists(item))
                return false;
            DiskPaths.Move(item, originalPath);
            File.Delete(match.Info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>$I file: version (8 bytes), size (8), deletion time (8), then the original path (version 2: length-prefixed UTF-16).</summary>
    static (string Path, DateTime Deleted)? ReadInfo(string infoFile)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoFile);
            if (bytes.Length < 28)
                return null;
            long version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(bytes, 16));
            string path = version >= 2
                ? Encoding.Unicode.GetString(bytes, 28, Math.Min(BitConverter.ToInt32(bytes, 24) * 2, bytes.Length - 28))
                : Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            return (path.TrimEnd('\0'), deleted);
        }
        catch
        {
            return null;
        }
    }
}
