using System.IO;

namespace FileStar.Services;

public static class AppPaths
{
    public const string AppName = "FileStar";

    static string? _dataDir;

    /// <summary>
    /// %AppData%\FileStar — settings, favorites, memos/tags and AI settings. On first use, files from the folder
    /// the app used before it was named FileStar (%AppData%\FileExplorer) are copied over so nothing is lost.
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null)
                return _dataDir;
            // Lets a UI test run of the app use a scratch folder instead of the user's real settings.
            string? overrideDir = Environment.GetEnvironmentVariable("FILESTAR_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return _dataDir = overrideDir;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, AppName);
            string legacy = Path.Combine(appData, "FileExplorer");
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(dir);
                    foreach (var file in Directory.EnumerateFiles(legacy, "*.json"))
                        File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
                }
            }
            catch { }
            return _dataDir = dir;
        }
    }

    public static string DataFile(string name) => Path.Combine(DataDir, name);
}
