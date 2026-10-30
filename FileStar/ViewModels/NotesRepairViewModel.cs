using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>A heading or a memo row in the 메모 정리 list.</summary>
public abstract class NotesRepairEntry : ObservableObject
{
}

public sealed class NotesRepairHeader(string text, bool isFirst) : NotesRepairEntry
{
    public string Text => text;
    public bool IsFirst => isFirst;
}

public sealed class NotesRepairRow(LostNote lost, bool isChecked) : NotesRepairEntry
{
    bool _isChecked = isChecked;

    public LostNote Lost => lost;

    public string Paths => lost.NewPath != null ? $"{lost.OldPath}\n→ {lost.NewPath}" : lost.OldPath;

    /// <summary>The memo's first line, on a line of its own under the paths.</summary>
    public string MemoLine => lost.Note.Summary.Length > 0 ? "\n메모: " + lost.Note.Summary : "";

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>도구 > 메모 정리: reattach memos to files moved outside FileStar, and clear memos of deleted files.</summary>
public sealed class NotesRepairViewModel : ObservableObject
{
    readonly INotesStore _notes;
    readonly Action _onChanged;
    string _status = "";

    /// <param name="onChanged">Called after memos were changed, so the main window can refresh.</param>
    public NotesRepairViewModel(INotesStore notes, Action onChanged)
    {
        _notes = notes;
        _onChanged = onChanged;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => Entries.OfType<NotesRepairRow>().Any());
    }

    public ObservableCollection<NotesRepairEntry> Entries { get; } = new();

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand ApplyCommand { get; }

    public async Task ScanAsync()
    {
        Status = "메모를 확인하는 중...";
        Entries.Clear();
        var snapshot = _notes.Snapshot();
        var result = await Task.Run(() => NotesRepair.Scan(snapshot));
        _notes.ApplyIds(result.NewIds);

        var moved = result.Lost.Where(l => l.NewPath != null).ToList();
        var missing = result.Lost.Where(l => l.NewPath == null).ToList();
        Status = result.Lost.Count == 0
            ? $"메모 {snapshot.Count:N0}개를 확인했습니다. 떨어진 메모가 없습니다."
            : $"메모 {snapshot.Count:N0}개 중 파일을 다시 찾은 것 {moved.Count:N0}개, 찾지 못한 것 {missing.Count:N0}개.";
        AddGroup("옮겨진 파일 — 체크한 항목은 메모를 새 위치로 다시 연결합니다", moved, isChecked: true);
        AddGroup("찾을 수 없는 파일 (삭제됐거나 다른 드라이브로 옮겨짐) — 체크한 항목은 메모를 지웁니다", missing, isChecked: false);
        CommandManager.InvalidateRequerySuggested();
        _onChanged();
    }

    void AddGroup(string header, List<LostNote> notes, bool isChecked)
    {
        if (notes.Count == 0)
            return;
        Entries.Add(new NotesRepairHeader(header, isFirst: Entries.Count == 0));
        foreach (var lost in notes)
            Entries.Add(new NotesRepairRow(lost, isChecked));
    }

    async Task ApplyAsync()
    {
        var chosen = Entries.OfType<NotesRepairRow>().Where(r => r.IsChecked).Select(r => r.Lost).ToList();
        int relinked = 0;
        foreach (var lost in chosen.Where(l => l.NewPath != null))
        {
            if (_notes.Relink(lost.OldPath, lost.NewPath!))
                relinked++;
        }
        var removed = chosen.Where(l => l.NewPath == null).Select(l => l.OldPath).ToList();
        _notes.Remove(removed);
        await ScanAsync();
        Status = $"다시 연결 {relinked:N0}개, 지운 메모 {removed.Count:N0}개. " + Status;
    }
}
