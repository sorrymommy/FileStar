using System.IO;

namespace FileStar.Services;

/// <summary>The file-or-folder questions every operation asks.</summary>
public static class DiskPaths
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Writes the whole file or nothing: the text goes to a temporary file first, which then replaces the old one,
    /// so a crash or power cut mid-write never leaves a half-written settings file behind.
    /// </summary>
    public static void WriteAllTextAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Renames or moves a file or folder (on the same drive).</summary>
    public static void Move(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }
}
