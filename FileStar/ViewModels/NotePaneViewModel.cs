using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>
/// The 메모·태그 tab: the memo and tags of one item (or of the current folder). The memo is read-only until
/// 수정 and written only on 저장; tag changes are saved right away.
/// </summary>
public sealed class NotePaneViewModel : ObservableObject
{
    const string EditingHint = "저장(Ctrl+S) / 취소(Esc)";

    readonly INotesStore _notes;
    readonly IDialogService _dialogs;
    string? _path;
    FileNote? _note;
    string _text = "", _savedText = "", _tagInput = "";
    bool _isEditing, _tagsDirty;

    public NotePaneViewModel(INotesStore notes, IDialogService dialogs)
    {
        _notes = notes;
        _dialogs = dialogs;
        EditCommand = new RelayCommand(BeginEdit, () => CanEdit && !IsEditing);
        SaveCommand = new RelayCommand(SaveEdit, () => IsEditing);
        CancelCommand = new RelayCommand(CancelEdit, () => IsEditing);
        DeleteCommand = new RelayCommand(Delete, () => _note != null);
        ConfirmCommand = new RelayCommand(Confirm, () => IsStale);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);
        OpenTagCommand = new RelayCommand<string>(tag => TagOpened?.Invoke(tag));
    }

    /// <summary>A memo or its tags were written; rows showing <c>path</c> should show the new note.</summary>
    public event Action<string, FileNote?>? Saved;

    /// <summary>A tag chip was clicked: show everything carrying it.</summary>
    public event Action<string>? TagOpened;

    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand OpenTagCommand { get; }

    /// <summary>The item whose memo is shown; null for 내 PC, result lists and multiple selections.</summary>
    public string? Path => _path;

    public bool CanEdit => _path != null;

    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>The memo box; edits only count while <see cref="IsEditing"/>.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
                OnStateChanged();
        }
    }

    public string TagInput
    {
        get => _tagInput;
        set => Set(ref _tagInput, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (Set(ref _isEditing, value))
                OnStateChanged();
        }
    }

    public bool IsDirty => _isEditing && _text != _savedText;

    public bool ShowPlaceholder => _text.Length == 0 && _path != null;

    public string StatusText => _isEditing
        ? (IsDirty ? "수정 중 (저장 안 됨) · " : "수정 중 · ") + EditingHint
        : _note == null ? "" : $"저장됨 · {_note.Updated:yyyy-MM-dd HH:mm}";

    /// <summary>"메모 갱신 필요": the file was saved after this memo was written or last confirmed.</summary>
    public bool IsStale => _staleText.Length > 0;

    public string StaleText => _staleText;

    string _staleText = "";

    /// <summary>Checks the file's time against the memo (on disk, so only when the memo itself changes).</summary>
    void UpdateStale()
    {
        _staleText = "";
        if (_path == null || _note == null || !File.Exists(_path))
            return;
        var written = File.GetLastWriteTime(_path);
        if (_note.IsStaleFor(written, isDirectory: false))
            _staleText = $"메모를 쓴 뒤({_note.Updated:yyyy-MM-dd HH:mm})에 파일이 수정되었습니다({written:yyyy-MM-dd HH:mm}). " +
                         "메모가 지금 내용과 다를 수 있습니다 — '수정'이나 'AI 요약'으로 고치거나, 맞으면 '확인함'을 누르세요.";
    }

    /// <summary>Points the editor at <paramref name="path"/> (null: nothing to write a memo on), finishing any pending edit first.</summary>
    public void ShowFor(string? path)
    {
        ResolvePendingEdit();
        CommitTags();
        _path = path;
        LoadNote();
    }

    /// <summary>Re-reads the memo of the same item (it was changed elsewhere, e.g. by the AI window).</summary>
    public void Reload()
    {
        if (!_isEditing)
            LoadNote();
    }

    void LoadNote()
    {
        SetNote(_path == null ? null : _notes.Get(_path));
        _savedText = _text = _note?.Text ?? "";
        _isEditing = false;
        _tagsDirty = false;
        Tags.Clear();
        foreach (var tag in _note?.TagList ?? Array.Empty<string>())
            Tags.Add(tag);
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(CanEdit));
        OnStateChanged();
    }

    void SetNote(FileNote? note)
    {
        _note = note;
        UpdateStale();
    }

    void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleText));
        CommandManager.InvalidateRequerySuggested();
    }

    public void BeginEdit()
    {
        if (_path != null)
            IsEditing = true;
    }

    public void SaveEdit()
    {
        if (!_isEditing || _path == null)
            return;
        _isEditing = false;
        Store(_text);
        OnPropertyChanged(nameof(IsEditing));
        OnStateChanged();
    }

    public void CancelEdit()
    {
        if (!_isEditing)
            return;
        _isEditing = false;
        _text = _savedText;
        SetNote(_path == null ? null : _notes.Get(_path));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsEditing));
        OnStateChanged();
    }

    /// <summary>Before the editor moves on (other item, pane hidden, app closing): ask what to do with an unsaved edit.</summary>
    public void ResolvePendingEdit()
    {
        if (!_isEditing)
            return;
        if (IsDirty && _dialogs.Confirm("수정한 메모를 저장할까요?\n\n'아니요'를 누르면 수정한 내용을 버립니다.", "메모 저장"))
            SaveEdit();
        else
            CancelEdit();
    }

    /// <summary>Saves a tag change right away. The memo text kept on disk is reused, so an unsaved memo edit is not saved with it.</summary>
    public void CommitTags()
    {
        if (!_tagsDirty || _path == null)
            return;
        _tagsDirty = false;
        Store(_savedText);
    }

    void Store(string text)
    {
        SetNote(_notes.Set(_path!, text, Tags));
        _savedText = text;
        Saved?.Invoke(_path!, _note);
        OnStateChanged();
    }

    void Delete()
    {
        if (_path == null || !_dialogs.Confirm("이 항목의 메모와 태그를 모두 지울까요? (파일은 그대로 남습니다)", "메모·태그 삭제"))
            return;
        _isEditing = false;
        _text = "";
        Tags.Clear();
        _tagsDirty = false;
        Store("");
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsEditing));
        OnStateChanged();
    }

    void Confirm()
    {
        if (_path == null || _notes.Confirm(_path) is not FileNote note)
            return;
        SetNote(note);
        Saved?.Invoke(_path, note);
        OnStateChanged();
    }

    // ── Tags ──

    /// <summary>Tags used elsewhere that this item doesn't have yet, for the tag box's dropdown.</summary>
    public List<string> TagSuggestions() =>
        _notes.AllTags().Where(t => !Tags.Contains(t, StringComparer.CurrentCultureIgnoreCase)).ToList();

    /// <summary>Adds "#a, b" style input as tags and saves them.</summary>
    public void AddTags(string input)
    {
        if (_path == null)
            return;
        bool added = false;
        foreach (var tag in NoteTags.Parse(input))
        {
            if (Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
                continue;
            Tags.Add(tag);
            added = true;
        }
        if (!added)
            return;
        _tagsDirty = true;
        CommitTags();
    }

    /// <summary>The tag box's Enter: adds what was typed and clears it.</summary>
    public void AddTypedTags()
    {
        AddTags(TagInput);
        TagInput = "";
    }

    void RemoveTag(string tag)
    {
        if (!Tags.Remove(tag))
            return;
        _tagsDirty = true;
        CommitTags();
    }
}
