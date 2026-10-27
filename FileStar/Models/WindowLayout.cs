namespace FileStar.Models;

/// <summary>What only the window knows about itself, handed to the ViewModel when settings are saved.</summary>
public sealed record WindowLayout(
    double Left,
    double Top,
    double Width,
    double Height,
    bool Maximized,
    double TreeWidth,
    double NotePaneWidth,
    double DualPaneRatio,
    List<ColumnSetting> Columns);
