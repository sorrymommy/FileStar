using System.IO;

namespace FileStar.Ai;

/// <summary>The readable files inside folders picked for "AI로 메모·태그 만들기".</summary>
public sealed record AiFolderScan(int FolderCount, List<string> Direct, List<string> All, bool Truncated);

public static class AiFileCollector
{
    /// <summary>Upper bound on the scan, so picking a drive root doesn't walk the whole disk.</summary>
    public const int MaxFiles = 5000;

    /// <summary>Walks the folders off the UI thread: the files directly inside them, and (up to <see cref="MaxFiles"/>) at any depth.</summary>
    public static async Task<AiFolderScan> ScanAsync(IReadOnlyList<string> folders, FileAttributes skip, IDocumentReader reader)
    {
        var (direct, all) = await Task.Run(() => (
            folders.SelectMany(f => SafeFiles(f, skip, recurse: false, reader)).ToList(),
            folders.SelectMany(f => SafeFiles(f, skip, recurse: true, reader)).Take(MaxFiles + 1).ToList()));
        bool truncated = all.Count > MaxFiles;
        if (truncated)
            all.RemoveAt(all.Count - 1);
        return new AiFolderScan(folders.Count, direct, all, truncated);
    }

    /// <summary>Readable files in a folder (stops after <see cref="MaxFiles"/>+1, so a drive root doesn't scan forever).</summary>
    static IEnumerable<string> SafeFiles(string folder, FileAttributes skip, bool recurse, IDocumentReader reader)
    {
        try
        {
            var scan = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = skip, RecurseSubdirectories = recurse };
            return Directory.EnumerateFiles(folder, "*", scan).Where(reader.IsReadable).Take(MaxFiles + 1).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
