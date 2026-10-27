using System.IO;
using System.Text.Json;

namespace FileStar;

public sealed record ColumnSetting(string Key, bool Visible, double Width);

/// <summary>Window layout and open tabs, saved to %AppData%\FileStar\settings.json.</summary>
public sealed class AppSettings
{
    public List<string> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }

    /// <summary>Indexes (into <see cref="Tabs"/>) of the pinned tabs.</summary>
    public List<int>? PinnedTabs { get; set; } = new();
    public bool ShowHidden { get; set; }
    public double TreeWidth { get; set; } = 320;
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }
    public string SortKey { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public bool NotePaneVisible { get; set; } = true;
    public double NotePaneWidth { get; set; } = 340;
    public bool DarkMode { get; set; }

    /// <summary>Window position (null = let Windows place it, as on first start).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }

    /// <summary>두 창 보기: open or not, its folder, and its share of the list area.</summary>
    public bool DualPaneVisible { get; set; }
    public string? DualPanePath { get; set; }
    public double DualPaneRatio { get; set; } = 0.5;

    /// <summary>이동 > 최근 폴더 / 최근 파일 (most recent first).</summary>
    public List<string> RecentFolders { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Memo/tag markers in the file list and tree (toolbar toggle).</summary>
    public bool ShowNoteMarks { get; set; } = true;

    /// <summary>검색 탭 options last used: n 이름, e 확장자, t 태그, m 메모, s 하위 폴더 포함.</summary>
    public string? SearchOptions { get; set; } = "ntms";

    /// <summary>Info pane tab: 0 메모·태그, 1 속성, 2 미리보기.</summary>
    public int InfoTab { get; set; }

    /// <summary>속성 tab: width of the label column, and the properties the user chose to hide.</summary>
    public double PropLabelWidth { get; set; } = 110;
    public List<string> HiddenProps { get; set; } = new();

    /// <summary>File-list columns in display order (null = defaults).</summary>
    public List<ColumnSetting>? Columns { get; set; }

    static string FilePath => AppPaths.DataFile("settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
