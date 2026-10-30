using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>One file in the AI run: its status, and the editable memo/tags the AI proposed.</summary>
public sealed class AiJob : ObservableObject
{
    string _status = "대기";
    string _summary = "", _tagsText = "";
    string? _note;
    bool _apply, _hasResult, _isError;

    public required string FullPath { get; init; }
    public string FileName => Path.GetFileName(FullPath);
    public string FolderPath => Path.GetDirectoryName(FullPath) ?? "";

    public string Status { get => _status; set => Set(ref _status, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string TagsText { get => _tagsText; set => Set(ref _tagsText, value); }
    public bool Apply { get => _apply; set => Set(ref _apply, value); }
    public bool IsError { get => _isError; set => Set(ref _isError, value); }

    /// <summary>What reading the file left out ("앞부분 120,000자만 분석" …).</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
                OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(_note);

    public bool HasResult
    {
        get => _hasResult;
        set => Set(ref _hasResult, value);
    }
}

/// <summary>
/// "AI로 메모·태그 만들기": runs the chosen AI over the files one by one, lets the user check and edit what it
/// proposed, then saves the checked results (appending to existing memos, merging tags).
/// </summary>
public sealed class AiSummaryViewModel : ObservableObject
{
    readonly AiSettings _settings;
    readonly IAiSettingsStore _store;
    readonly IAiSummarizer _summarizer;
    readonly IDocumentReader _reader;
    readonly INotesStore _notes;
    readonly IWindowService _windows;
    readonly IDialogService _dialogs;
    readonly Action _onSaved;
    IReadOnlyList<AiChoice> _choices = Array.Empty<AiChoice>();
    AiChoice? _selectedChoice;
    CancellationTokenSource? _cts;
    bool _isRunning;
    int _progressMax, _progressValue;
    string _summaryStatus = "결과를 확인·수정한 뒤 저장하세요. 이미 메모가 있으면 지우지 않고 아래에 덧붙이고, 태그는 합칩니다.";

    /// <param name="onSaved">Called after memos are written, so the main window can refresh.</param>
    public AiSummaryViewModel(IEnumerable<string> paths, IAiSettingsStore store, IAiSummarizer summarizer, IDocumentReader reader,
        INotesStore notes, IWindowService windows, IDialogService dialogs, Action onSaved)
    {
        _store = store;
        _settings = store.Load();
        _summarizer = summarizer;
        _reader = reader;
        _notes = notes;
        _windows = windows;
        _dialogs = dialogs;
        _onSaved = onSaved;
        Jobs = new(paths.Select(p => new AiJob { FullPath = p }));
        foreach (var job in Jobs)
            job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AiJob.Apply))
                    CommandManager.InvalidateRequerySuggested();
            };
        FillChoices();
        StartCommand = new AsyncRelayCommand(StartAsync, () => !_isRunning);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => _isRunning);
        ApplyCommand = new RelayCommand(Apply, () => !_isRunning && Jobs.Any(j => j.Apply && j.HasResult));
        SettingsCommand = new RelayCommand(OpenSettings, () => !_isRunning);
    }

    public ObservableCollection<AiJob> Jobs { get; }

    public string Title => $"AI로 메모·태그 만들기 — {Jobs.Count:N0}개 파일";

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SettingsCommand { get; }

    public IReadOnlyList<AiChoice> Choices
    {
        get => _choices;
        private set => Set(ref _choices, value);
    }

    public AiChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (!Set(ref _selectedChoice, value))
                return;
            OnPropertyChanged(nameof(ModelText));
            OnPropertyChanged(nameof(PrivacyText));
            OnPropertyChanged(nameof(IsPrivate));
        }
    }

    AiChoice Choice => _selectedChoice ?? new AiChoice(AiProvider.Claude);

    public string ModelText => _settings.ModelFor(Choice) is { Length: > 0 } model ? model : "(모델 미설정 — 설정에서 선택)";

    /// <summary>The chosen AI runs on this PC: nothing leaves it.</summary>
    public bool IsPrivate => Choice.IsLocal;

    public string PrivacyText => IsPrivate
        ? $"이 PC의 '{Choice.Server!.Name}'(으)로 처리합니다. 문서가 외부로 전송되지 않습니다. (PDF는 지원하지 않음)"
        : $"⚠ 시작하면 아래 파일들의 내용이 {Choice.Destination}로 전송됩니다. 외부로 보내면 안 되는 문서는 이 PC에서 돌아가는 AI를 선택하세요."
          + (Choice.Provider == AiProvider.Custom ? " (PDF는 지원하지 않음)" : "");

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value))
                return;
            OnPropertyChanged(nameof(StartText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StartText => !_isRunning && Jobs.All(j => j.HasResult) ? "다시 실행" : "시작";

    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public string SummaryStatus { get => _summaryStatus; private set => Set(ref _summaryStatus, value); }

    /// <summary>Lists every AI and selects the saved default.</summary>
    void FillChoices()
    {
        var choices = _settings.Choices();
        var preferred = _settings.DefaultChoice();
        Choices = choices;
        SelectedChoice = choices.FirstOrDefault(c => c.Matches(preferred)) ?? choices[0];
    }

    void OpenSettings()
    {
        if (_windows.ShowAiSettings(_settings))
            FillChoices();
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(PrivacyText));
    }

    /// <summary>The AI picked for a run becomes the default next time.</summary>
    void RememberChoice(AiChoice choice)
    {
        if (!_settings.Remember(choice))
            return;
        try
        {
            _store.Save(_settings);
        }
        catch { }   // not being able to remember the choice shouldn't stop the run
    }

    async Task StartAsync()
    {
        var choice = Choice;
        RememberChoice(choice);
        var knownTags = _notes.AllTags();
        int maxChars = _summarizer.MaxChars(choice);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var pending = Jobs.Where(j => !j.HasResult).ToList();
        ProgressMax = pending.Count;
        ProgressValue = 0;
        IsRunning = true;
        int done = 0, failed = 0;
        foreach (var job in pending)
        {
            if (token.IsCancellationRequested)
                break;
            job.IsError = false;
            try
            {
                job.Status = "내용 읽는 중...";
                var content = await Task.Run(() => _reader.Read(job.FullPath, maxChars), token);
                job.Note = content.Note;

                job.Status = "AI 요약 중...";
                var result = await _summarizer.SummarizeAsync(_settings, choice, content, knownTags, token);
                job.Summary = result.Summary;
                job.TagsText = string.Join(", ", result.Tags);
                job.HasResult = true;
                job.Apply = true;
                job.Status = "완료";
                done++;
            }
            catch (OperationCanceledException)
            {
                job.Status = "중지됨";
                break;
            }
            catch (Exception ex)
            {
                job.Status = "실패: " + ex.Message;
                job.IsError = true;
                failed++;
            }
            ProgressValue++;
        }
        IsRunning = false;
        SummaryStatus = $"완료 {done}개, 실패 {failed}개. 결과를 확인·수정한 뒤 '체크한 항목 저장'을 누르세요. " +
                        "이미 메모가 있으면 지우지 않고 아래에 덧붙이고, 태그는 합칩니다.";
    }

    void Apply()
    {
        int saved = 0;
        foreach (var job in Jobs.Where(j => j.Apply && j.HasResult))
        {
            var existing = _notes.Get(job.FullPath);
            string summary = job.Summary.Trim();
            // Never discard what the user wrote by hand: append the AI memo under a dated marker.
            string text = string.IsNullOrWhiteSpace(existing?.Text)
                ? summary
                : existing!.Text.TrimEnd() + $"\n\n[AI 요약 {DateTime.Now:yyyy-MM-dd}]\n" + summary;
            var tags = (existing?.TagList ?? Array.Empty<string>()).Concat(NoteTags.Parse(job.TagsText));
            _notes.Set(job.FullPath, text, tags);
            job.Apply = false;
            job.Status = "저장됨";
            saved++;
        }
        _onSaved();
        SummaryStatus = $"{saved}개 파일에 메모·태그를 저장했습니다.";
    }

    /// <summary>The window is about to close: false if the user wants to keep unsaved results.</summary>
    public bool ConfirmClose() =>
        !Jobs.Any(j => j.Apply && j.HasResult) ||
        _dialogs.Confirm("저장하지 않은 AI 결과가 있습니다. 저장하지 않고 닫을까요?", "AI 요약");

    /// <summary>Stops a run in progress instead of leaving it working in the background.</summary>
    public void Closed() => _cts?.Cancel();
}
