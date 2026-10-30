namespace FileStar.ViewModels;

/// <summary>The status bar: item counts and selection, short messages, and the drive's free space.</summary>
public sealed class StatusBarViewModel(Func<DateTime>? clock = null) : ObservableObject
{
    static readonly TimeSpan NoteDuration = TimeSpan.FromSeconds(5);

    readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);
    string _text = "", _driveText = "";
    string? _note;
    DateTime _noteUntil;

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    public string DriveText
    {
        get => _driveText;
        private set => Set(ref _driveText, value);
    }

    public void ShowDrive(string location) => DriveText = Locations.DriveSpaceText(location);

    /// <summary>Replaces the text until the list next changes.</summary>
    public void Show(string message) => Text = message;

    /// <summary>A message that stays at the front for a few seconds, even if the list reloads meanwhile.</summary>
    public void Note(string note, FileListViewModel list)
    {
        _note = note;
        _noteUntil = _now() + NoteDuration;
        Update(list);
    }

    /// <summary>"12개 항목 (폴더 3, 파일 9) | 2개 선택됨 (1.2 MB)", or what the list is doing while it loads.</summary>
    public void Update(FileListViewModel list)
    {
        if (list.LoadingMessage is string loading)
        {
            Text = loading;
            return;
        }
        if (list.View == null)
            return;
        int folders = 0, files = 0;
        foreach (var item in list.VisibleItems)
        {
            if (item.IsDirectory) folders++;
            else files++;
        }
        string text = $"{folders + files:N0}개 항목 (폴더 {folders:N0}, 파일 {files:N0})";
        if (_note != null && _now() < _noteUntil)
            text = _note + "   |   " + text;
        var selected = list.Selected;
        if (selected.Count > 0)
        {
            long size = selected.Sum(i => i.Size ?? 0);
            text += $"   |   {selected.Count:N0}개 선택됨";
            if (size > 0)
                text += $" ({SizeFormat.Bytes(size)})";
        }
        Text = text + list.StatusSuffix;
    }
}
