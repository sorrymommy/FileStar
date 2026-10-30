using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>One line of the 속성 tab.</summary>
public abstract class PropertyEntry : ObservableObject
{
    /// <summary>The property's name, for hiding it from its right-click menu (null for headings).</summary>
    public string? Key { get; set; }

    public ICommand? HideCommand { get; set; }

    public string HideHeader => $"'{Key}' 숨기기";
}

/// <summary>A heading; shown only once one of its rows is.</summary>
public sealed class PropertySection(string title, bool isFirst) : PropertyEntry
{
    public string Title => title;
    public bool IsFirst => isFirst;
}

/// <summary>"이름 | 값"; the value can be selected and copied.</summary>
public sealed class PropertyRow(string label, string value) : PropertyEntry
{
    public string Label => label;
    public string Value => value;
}

/// <summary>The 특성 checkboxes (read-only).</summary>
public sealed class PropertyFlags(IReadOnlyList<PropertyFlag> flags) : PropertyEntry
{
    public IReadOnlyList<PropertyFlag> Flags => flags;
}

public sealed record PropertyFlag(string Label, bool IsOn);

/// <summary>A folder's size needs a full scan, so it is computed on request in the background.</summary>
public sealed class FolderSizeRow : PropertyEntry
{
    readonly string _path;
    CancellationTokenSource? _cts;
    string? _value;
    string _buttonText = "폴더 크기 계산";
    bool _isBusy;

    public FolderSizeRow(string path)
    {
        _path = path;
        CalculateCommand = new AsyncRelayCommand(CalculateAsync, () => !_isBusy && _value == null);
    }

    public string Label => "크기";

    /// <summary>Null until calculated; the row shows the button until then.</summary>
    public string? Value
    {
        get => _value;
        private set => Set(ref _value, value);
    }

    public string ButtonText
    {
        get => _buttonText;
        private set => Set(ref _buttonText, value);
    }

    public ICommand CalculateCommand { get; }

    public void Cancel() => _cts?.Cancel();

    async Task CalculateAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _isBusy = true;
        ButtonText = "계산 중...";
        try
        {
            var (bytes, files, folders) = await Task.Run(() => FileProperties.FolderSize(_path, cts.Token), cts.Token);
            Value = $"{SizeFormat.Bytes(bytes)} ({bytes:N0} 바이트)\n파일 {files:N0}개, 폴더 {folders:N0}개";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ButtonText = "계산 실패: " + ex.Message;
        }
        finally
        {
            _isBusy = false;
        }
    }
}

/// <summary>What the 속성 tab describes.</summary>
public abstract record PropertiesTarget
{
    /// <summary>One file or folder.</summary>
    public sealed record Item(string Path) : PropertiesTarget;

    /// <summary>Several selected items.</summary>
    public sealed record Selection(IReadOnlyList<FileItem> Items) : PropertiesTarget;

    /// <summary>내 PC: a summary of every drive in the list.</summary>
    public sealed record Drives(IReadOnlyList<FileItem> Items) : PropertiesTarget;
}

/// <summary>
/// The 속성 tab: general facts, dates, attributes and Windows' own details for one item. The user can hide
/// individual properties and widen the name column; both are remembered with the window settings.
/// </summary>
public sealed class PropertiesViewModel : ObservableObject
{
    public const string AttributesKey = "특성";
    public const double DefaultLabelWidth = 110;

    readonly HashSet<string> _hidden = new();
    readonly List<string> _seen = new();
    PropertiesTarget? _target;
    string? _pendingSection;
    double _labelWidth = DefaultLabelWidth;

    public PropertiesViewModel()
    {
        HideCommand = new RelayCommand<string>(label => SetVisible(label, false));
    }

    /// <summary>Hidden properties or the name column width changed (saved with the settings).</summary>
    public event Action? SettingsChanged;

    public ObservableCollection<PropertyEntry> Entries { get; } = new();

    public ICommand HideCommand { get; }

    public double LabelWidth
    {
        get => _labelWidth;
        set => Set(ref _labelWidth, Math.Clamp(value, 50, 400));
    }

    public IReadOnlyCollection<string> HiddenLabels => _hidden;

    public void LoadSettings(double labelWidth, IEnumerable<string> hidden)
    {
        LabelWidth = labelWidth;
        _hidden.Clear();
        _hidden.UnionWith(hidden);
    }

    /// <summary>The splitter was dragged (called once at the end, to save).</summary>
    public void CommitLabelWidth() => SettingsChanged?.Invoke();

    /// <summary>Every property this item has (shown or hidden) plus hidden ones it lacks, for the chooser menu.</summary>
    public IReadOnlyList<(string Label, bool Visible)> ChooserItems() =>
        _seen.Concat(_hidden.Where(h => !_seen.Contains(h)).OrderBy(h => h))
            .Select(label => (label, !_hidden.Contains(label)))
            .ToList();

    public bool AnyHidden => _hidden.Count > 0;

    public void SetVisible(string label, bool visible)
    {
        if (visible ? !_hidden.Remove(label) : !_hidden.Add(label))
            return;
        Refresh();
        SettingsChanged?.Invoke();
    }

    public void ShowAll()
    {
        _hidden.Clear();
        Refresh();
        SettingsChanged?.Invoke();
    }

    public void ResetLabelWidth()
    {
        LabelWidth = DefaultLabelWidth;
        SettingsChanged?.Invoke();
    }

    public void Show(PropertiesTarget? target)
    {
        _target = target;
        Refresh();
    }

    void Refresh()
    {
        foreach (var row in Entries.OfType<FolderSizeRow>())
            row.Cancel();
        Entries.Clear();
        _seen.Clear();
        _pendingSection = null;
        switch (_target)
        {
            case PropertiesTarget.Drives drives:
                Section("드라이브");
                foreach (var drive in drives.Items.Where(i => i.IsDrive))
                    Row(drive.Name, drive.TypeName);
                break;
            case PropertiesTarget.Selection selection:
                var items = selection.Items;
                Section("선택 항목");
                Row("개수", $"{items.Count:N0}개 (폴더 {items.Count(i => i.IsDirectory):N0}, 파일 {items.Count(i => !i.IsDirectory):N0})");
                long total = items.Sum(i => i.Size ?? 0);
                Row("크기", $"{SizeFormat.Bytes(total)} ({total:N0} 바이트)");
                break;
            case PropertiesTarget.Item item:
                AddItem(item.Path);
                break;
        }
    }

    void AddItem(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        try { info.Refresh(); }
        catch { }
        bool isDirectory = info is DirectoryInfo;

        Section("일반");
        Row("DOS 이름", FileProperties.ShortName(path));
        Row("유형", ShellInfo.GetTypeName(isDirectory ? "" : Path.GetExtension(path), isDirectory));
        Row("위치", Path.GetDirectoryName(path) ?? path);
        if (info is FileInfo file && file.Exists)
        {
            Row("크기", $"{SizeFormat.Bytes(file.Length)} ({file.Length:N0} 바이트)");
            Row("디스크 할당 크기", FileProperties.SizeOnDisk(path) is var (bytes, clusters)
                ? $"{SizeFormat.Bytes(bytes)} ({bytes:N0} 바이트), {clusters:N0} 클러스터"
                : "-");
        }
        else if (isDirectory)
            Add("크기", new FolderSizeRow(path));

        Section("날짜");
        Row("만든 날짜", FormatDate(() => info.CreationTime));
        Row("수정한 날짜", FormatDate(() => info.LastWriteTime));
        Row("액세스한 날짜", FormatDate(() => info.LastAccessTime));

        Section(AttributesKey);
        uint attributes = FileProperties.RawAttributes(path);
        Add(AttributesKey, new PropertyFlags(FileProperties.Attributes
            .Select(a => new PropertyFlag(a.Label, (attributes & a.Flag) != 0))
            .ToList()));

        var details = FileProperties.ShellDetails(path);
        if (details.Count > 0)
        {
            Section("상세 정보 (Windows 속성)");
            foreach (var (name, value) in details)
                Row(name, value);
        }
    }

    static string FormatDate(Func<DateTime> getter)
    {
        try { return getter().ToString("yyyy-MM-dd tt h:mm:ss"); }
        catch { return "-"; }
    }

    /// <summary>Sections appear only once one of their rows does, so hiding every row hides the heading too.</summary>
    void Section(string title) => _pendingSection = title;

    void Row(string label, string value) => Add(label, new PropertyRow(label, value));

    /// <summary>Adds an entry under <paramref name="label"/>, unless the user hid that property.</summary>
    void Add(string label, PropertyEntry entry)
    {
        if (!_seen.Contains(label))
            _seen.Add(label);
        if (_hidden.Contains(label))
            return;
        if (_pendingSection != null)
        {
            Entries.Add(new PropertySection(_pendingSection, isFirst: Entries.Count == 0));
            _pendingSection = null;
        }
        entry.Key = label;
        entry.HideCommand = HideCommand;
        Entries.Add(entry);
    }
}
