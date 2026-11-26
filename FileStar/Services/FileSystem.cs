using System.IO;

namespace FileStar.Services;

/// <summary>Reads folder listings, honoring the user's "숨김 파일 표시" choice.</summary>
public interface IFileSystem
{
    bool ShowHidden { get; set; }

    /// <summary>Hidden and system items, unless the user chose to see them.</summary>
    FileAttributes AttributesToSkip { get; }

    List<FileSystemInfo> GetEntries(string path);

    /// <summary>Subfolders sorted the way Explorer sorts names; empty if the folder can't be read.</summary>
    List<DirectoryInfo> GetSubfolders(string path);

    /// <summary>As <see cref="GetSubfolders"/>, but null if the folder can't be read (rather than empty).</summary>
    List<DirectoryInfo>? TryGetSubfolders(string path);

    bool HasSubfolders(string path);
}

public sealed class LocalFileSystem : IFileSystem
{
    public bool ShowHidden { get; set; }

    public FileAttributes AttributesToSkip => ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System;

    EnumerationOptions Options => new() { IgnoreInaccessible = true, AttributesToSkip = AttributesToSkip };

    // A computer on the network ("\\NAME") isn't a folder Windows can list; its shared folders are what it holds.

    public List<FileSystemInfo> GetEntries(string path)
    {
        if (!Locations.IsServer(path))
            return new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options).ToList();
        return Shares(path)?.Cast<FileSystemInfo>().ToList() ?? throw new IOException($"'{path}'에 연결할 수 없습니다.");
    }

    public List<DirectoryInfo> GetSubfolders(string path) => TryGetSubfolders(path) ?? new();

    static List<DirectoryInfo>? Shares(string server) => NetworkShell.DiskShares(server)?.Select(s => new DirectoryInfo(s)).ToList();

    public List<DirectoryInfo>? TryGetSubfolders(string path)
    {
        if (Locations.IsServer(path))
            return Shares(path);
        try
        {
            var list = new DirectoryInfo(path).EnumerateDirectories("*", Options).ToList();
            list.Sort((a, b) => Native.StrCmpLogicalW(a.Name, b.Name));
            return list;
        }
        catch
        {
            return null;
        }
    }

    public bool HasSubfolders(string path)
    {
        if (Locations.IsServer(path))
            return true;   // found out when it is opened: asking for its shares is a network round trip
        try { return Directory.EnumerateDirectories(path, "*", Options).Any(); }
        catch { return false; }
    }
}
