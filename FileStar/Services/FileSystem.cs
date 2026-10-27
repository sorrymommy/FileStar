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

    bool HasSubfolders(string path);
}

public sealed class LocalFileSystem : IFileSystem
{
    public bool ShowHidden { get; set; }

    public FileAttributes AttributesToSkip => ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System;

    EnumerationOptions Options => new() { IgnoreInaccessible = true, AttributesToSkip = AttributesToSkip };

    public List<FileSystemInfo> GetEntries(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options).ToList();

    public List<DirectoryInfo> GetSubfolders(string path)
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

    public bool HasSubfolders(string path)
    {
        try { return Directory.EnumerateDirectories(path, "*", Options).Any(); }
        catch { return false; }
    }
}
