using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FileStar;

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

public partial class AiSummaryWindow : Window
{
    readonly ObservableCollection<AiJob> _jobs;
    readonly AiSettings _settings;
    readonly Action _onSaved;
    CancellationTokenSource? _cts;

    /// <param name="onSaved">Called after memos are written, so the main window can refresh.</param>
    public AiSummaryWindow(IEnumerable<string> paths, Action onSaved)
    {
        InitializeComponent();
        _settings = AiSettings.Load();
        _onSaved = onSaved;
        _jobs = new(paths.Select(p => new AiJob { FullPath = p }));
        JobList.ItemsSource = _jobs;
        FillProviders();
        Title = $"AI로 메모·태그 만들기 — {_jobs.Count:N0}개 파일";
    }

    /// <summary>Lists every AI and selects the saved default.</summary>
    void FillProviders()
    {
        var choices = _settings.Choices();
        var preferred = _settings.DefaultChoice();
        ProviderBox.ItemsSource = choices;
        ProviderBox.SelectedItem = choices.FirstOrDefault(c => c.Provider == preferred.Provider && c.Server?.Id == preferred.Server?.Id)
                                   ?? choices[0];
    }

    /// <summary>The AI picked for a run becomes the default next time (saved to ai.json).</summary>
    void RememberChoice(AiChoice choice)
    {
        if (_settings.Provider == choice.Provider && (choice.Server == null || _settings.ServerId == choice.Server.Id))
            return;
        _settings.Provider = choice.Provider;
        if (choice.Server != null)
            _settings.ServerId = choice.Server.Id;
        try
        {
            _settings.Save();
        }
        catch { }   // not being able to remember the choice shouldn't stop the run
    }

    AiChoice SelectedChoice => ProviderBox.SelectedItem as AiChoice ?? new AiChoice(AiProvider.Claude);

    void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderText();

    void UpdateProviderText()
    {
        var choice = SelectedChoice;
        string model = _settings.ModelFor(choice);
        ModelText.Text = string.IsNullOrEmpty(model) ? "(모델 미설정 — 설정에서 선택)" : model;
        if (choice.Server is { IsLocal: true } local)
        {
            PrivacyText.Text = $"이 PC의 '{local.Name}'(으)로 처리합니다. 문서가 외부로 전송되지 않습니다. (PDF는 지원하지 않음)";
            PrivacyText.SetResourceReference(TextBlock.ForegroundProperty, "Th.Ok");
            return;
        }
        string destination = choice.Provider switch
        {
            AiProvider.OpenAI => "OpenAI(ChatGPT) 서버",
            AiProvider.Gemini => "Google(Gemini) 서버",
            AiProvider.Claude => "Anthropic(Claude) 서버",
            _ => $"'{choice.Server?.Name}' 서버({HostOf(choice.Server?.Url)})",
        };
        PrivacyText.Text = $"⚠ 시작하면 아래 파일들의 내용이 {destination}로 전송됩니다. 외부로 보내면 안 되는 문서는 이 PC에서 돌아가는 AI를 선택하세요."
                           + (choice.Provider == AiProvider.Custom ? " (PDF는 지원하지 않음)" : "");
        PrivacyText.SetResourceReference(TextBlock.ForegroundProperty, "Th.Warn");
    }

    static string HostOf(string? url) => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Authority : url ?? "";

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new AiSettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            FillProviders();
            UpdateProviderText();
        }
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var choice = SelectedChoice;
        var settings = _settings;
        RememberChoice(choice);
        var knownTags = NotesStore.AllTags();
        int maxChars = AiSummarizer.MaxChars(choice);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetRunning(true);
        int done = 0, failed = 0;
        Progress.Maximum = _jobs.Count(j => !j.HasResult);
        Progress.Value = 0;

        foreach (var job in _jobs.Where(j => !j.HasResult).ToList())
        {
            if (token.IsCancellationRequested)
                break;
            job.IsError = false;
            try
            {
                job.Status = "내용 읽는 중...";
                var content = await Task.Run(() => DocumentReader.Read(job.FullPath, maxChars), token);
                job.Note = content.Note;

                job.Status = "AI 요약 중...";
                var result = await AiSummarizer.SummarizeAsync(settings, choice, content, knownTags, token);
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
            Progress.Value++;
        }

        SetRunning(false);
        SummaryStatus.Text = $"완료 {done}개, 실패 {failed}개. 결과를 확인·수정한 뒤 '체크한 항목 저장'을 누르세요. " +
                             "이미 메모가 있으면 지우지 않고 아래에 덧붙이고, 태그는 합칩니다.";
        ApplyButton.IsEnabled = _jobs.Any(j => j.HasResult);
    }

    void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // IsCancel only closes dialogs opened with ShowDialog; this window is modeless, so close it explicitly.
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();   // stop a run in progress instead of leaving it working in the background
        base.OnClosed(e);
    }

    void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ProviderBox.IsEnabled = SettingsButton.IsEnabled = !running;
        ApplyButton.IsEnabled = !running && _jobs.Any(j => j.HasResult);
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running && _jobs.All(j => j.HasResult))
            StartButton.Content = "다시 실행";
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        int saved = 0;
        foreach (var job in _jobs.Where(j => j.Apply && j.HasResult))
        {
            var existing = NotesStore.Get(job.FullPath);
            string summary = job.Summary.Trim();
            // Never discard what the user wrote by hand: append the AI memo under a dated marker.
            string text = string.IsNullOrWhiteSpace(existing?.Text)
                ? summary
                : existing!.Text.TrimEnd() + $"\n\n[AI 요약 {DateTime.Now:yyyy-MM-dd}]\n" + summary;
            var tags = (existing?.TagList ?? Array.Empty<string>()).Concat(NotesStore.ParseTags(job.TagsText));
            NotesStore.Set(job.FullPath, text, tags);
            job.Apply = false;
            job.Status = "저장됨";
            saved++;
        }
        _onSaved();
        SummaryStatus.Text = $"{saved}개 파일에 메모·태그를 저장했습니다.";
        ApplyButton.IsEnabled = _jobs.Any(j => j.Apply);
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _cts?.Cancel();
        if (_jobs.Any(j => j.Apply && j.HasResult) &&
            MessageBox.Show(this, "저장하지 않은 AI 결과가 있습니다. 저장하지 않고 닫을까요?", "AI 요약",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
}
