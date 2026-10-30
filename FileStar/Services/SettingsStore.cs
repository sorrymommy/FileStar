using System.IO;
using System.Text.Json;

namespace FileStar.Services;

/// <summary>Reads and writes the window settings (<see cref="AppSettings"/>).</summary>
public interface ISettingsStore
{
    /// <summary>The saved settings, or defaults when there are none or they can't be read.</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}

public sealed class JsonSettingsStore(string filePath) : ISettingsStore
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            DiskPaths.WriteAllTextAtomically(filePath, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch { }
    }
}
