using System.IO;

namespace FileStar.Tests.Support;

/// <summary>A scratch folder for one test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileStar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content = "x")
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Folder(string relative) => Directory.CreateDirectory(Combine(relative)).FullName;

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { }
    }
}
