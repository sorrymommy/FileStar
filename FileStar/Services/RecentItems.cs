namespace FileStar.Services;

/// <summary>Recently visited folders and recently opened files (most recent first), kept with the settings.</summary>
public interface IRecentItems
{
    IReadOnlyList<string> Folders { get; }
    IReadOnlyList<string> Files { get; }

    void Load(IEnumerable<string> folders, IEnumerable<string> files);
    void AddFolder(string path);
    void AddFile(string path);
    void Clear();
}

public sealed class RecentItems : IRecentItems
{
    const int Limit = 20;

    readonly List<string> _folders = new();
    readonly List<string> _files = new();

    public IReadOnlyList<string> Folders => _folders;
    public IReadOnlyList<string> Files => _files;

    public void Load(IEnumerable<string> folders, IEnumerable<string> files)
    {
        _folders.Clear();
        _folders.AddRange(folders);
        _files.Clear();
        _files.AddRange(files);
    }

    public void AddFolder(string path) => Add(_folders, path);
    public void AddFile(string path) => Add(_files, path);

    static void Add(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > Limit)
            list.RemoveRange(Limit, list.Count - Limit);
    }

    public void Clear()
    {
        _folders.Clear();
        _files.Clear();
    }
}
