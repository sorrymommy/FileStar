using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>One of the user's servers being edited (a copy: 취소 leaves the saved list untouched).</summary>
public sealed class AiServerViewModel : ObservableObject
{
    IReadOnlyList<string> _modelChoices = Array.Empty<string>();
    string? _status;

    public AiServerViewModel(AiServer server) => Server = server;

    public AiServer Server { get; }

    /// <summary>Name, kind or address changed: the default-AI list shows them.</summary>
    public event Action? LabelChanged;

    public string Name
    {
        get => Server.Name;
        set
        {
            if (Server.Name == value)
                return;
            Server.Name = value;
            OnPropertyChanged();
            OnChanged();
        }
    }

    public int KindIndex
    {
        get => (int)Server.Kind;
        set
        {
            var kind = value == 1 ? AiServerKind.OpenAiCompatible : AiServerKind.Ollama;
            if (Server.Kind == kind)
                return;
            Server.Kind = kind;
            // Swap in the usual address for the new kind if it is still the other kind's default.
            if (kind == AiServerKind.OpenAiCompatible && Url.Trim() == AiServer.OllamaDefaultUrl)
                Url = AiServer.OpenAiCompatibleDefaultUrl;
            else if (kind == AiServerKind.Ollama && Url.Trim() == AiServer.OpenAiCompatibleDefaultUrl)
                Url = AiServer.OllamaDefaultUrl;
            OnPropertyChanged();
            OnChanged();
        }
    }

    public string Url
    {
        get => Server.Url;
        set
        {
            if (Server.Url == value)
                return;
            Server.Url = value;
            OnPropertyChanged();
            OnChanged();
        }
    }

    /// <summary>Set by the view from its PasswordBox.</summary>
    public string ApiKey
    {
        get => Server.ApiKey;
        set => Server.ApiKey = value;
    }

    public string Model
    {
        get => Server.Model;
        set
        {
            if (Server.Model == value)
                return;
            Server.Model = value;
            OnPropertyChanged();
            OnChanged();   // a cloud model changes where documents go
        }
    }

    public IReadOnlyList<string> ModelChoices
    {
        get => _modelChoices;
        set => Set(ref _modelChoices, value);
    }

    /// <summary>The model list's outcome, or the usual hint for this kind of server.</summary>
    public string Status
    {
        get => _status ?? Hint;
        set => Set(ref _status, value);
    }

    string Hint =>
        (Server.Kind == AiServerKind.Ollama
            ? "주소 예: http://localhost:11434 (이 PC), http://192.168.0.20:11434 (다른 PC). 키는 보통 비워 둡니다."
            : "주소 예: http://localhost:1234/v1 (LM Studio), http://서버:8000/v1 (vLLM). 키가 필요한 서버만 입력하세요.")
        + (Server.IsCloudModel ? "\n⚠ 클라우드 모델입니다. 주소는 이 PC지만 문서 내용이 Ollama 클라우드(ollama.com)로 전송됩니다."
            : Server.HasLocalAddress ? ""
            : "\n⚠ 이 PC가 아닌 주소입니다. 문서 내용이 그 서버로 전송됩니다.")
        + "\nPDF는 직접 읽지 못합니다(Claude·ChatGPT·Gemini 사용).";

    void OnChanged()
    {
        _status = null;
        OnPropertyChanged(nameof(Status));
        LabelChanged?.Invoke();
    }

    public override string ToString() => Name;
}

/// <summary>도구 > AI 설정: the default AI, each cloud service's model and key, and the user's own servers.</summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    public const int ClaudeTab = 0, OpenAiTab = 1, GeminiTab = 2, ServersTab = 3;

    readonly AiSettings _settings;
    readonly IAiSettingsStore _store;
    readonly IAiModelCatalog _catalog;
    readonly IDialogService _dialogs;
    IReadOnlyList<AiChoice> _choices = Array.Empty<AiChoice>();
    AiChoice? _selectedDefault;
    AiServerViewModel? _selectedServer;
    IReadOnlyList<string> _openAiModels = AiSettings.OpenAiModels, _geminiModels = AiSettings.GeminiModels;
    string _claudeModel, _openAiModel, _geminiModel;
    string _openAiStatus = "", _geminiStatus = "";
    int _tabIndex;
    bool _refreshing;

    public AiSettingsViewModel(AiSettings settings, IAiSettingsStore store, IAiModelCatalog catalog, IDialogService dialogs)
    {
        _settings = settings;
        _store = store;
        _catalog = catalog;
        _dialogs = dialogs;
        _claudeModel = settings.ClaudeModel;
        _openAiModel = settings.OpenAiModel;
        _geminiModel = settings.GeminiModel;
        ApiKey = settings.ApiKey;
        OpenAiKey = settings.OpenAiKey;
        GeminiKey = settings.GeminiKey;
        foreach (var server in settings.Servers)
            AddServerViewModel(server.Clone());

        var preferred = settings.DefaultChoice();
        RefreshDefaults(preferred);
        _selectedServer = Servers.FirstOrDefault();
        _tabIndex = TabFor(preferred.Provider);

        LoadOpenAiModelsCommand = new AsyncRelayCommand(LoadOpenAiModelsAsync);
        LoadGeminiModelsCommand = new AsyncRelayCommand(LoadGeminiModelsAsync);
        LoadServerModelsCommand = new AsyncRelayCommand(LoadServerModelsAsync, () => _selectedServer != null);
        AddServerCommand = new RelayCommand(AddServer);
        RemoveServerCommand = new RelayCommand(RemoveServer, () => _selectedServer != null);
    }

    /// <summary>A new server was added: the view puts the cursor in its name box.</summary>
    public event Action? ServerAdded;

    public ICommand LoadOpenAiModelsCommand { get; }
    public ICommand LoadGeminiModelsCommand { get; }
    public ICommand LoadServerModelsCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand RemoveServerCommand { get; }

    public static IReadOnlyList<string> ClaudeModels => AiSettings.ClaudeModels;

    // ── Default AI ──

    public IReadOnlyList<AiChoice> DefaultChoices
    {
        get => _choices;
        private set => Set(ref _choices, value);
    }

    public AiChoice? SelectedDefault
    {
        get => _selectedDefault;
        set
        {
            if (!Set(ref _selectedDefault, value))
                return;
            OnPropertyChanged(nameof(DefaultHint));
            if (_refreshing || value == null)
                return;
            // Jump to the chosen AI's tab so its settings are right there.
            TabIndex = TabFor(value.Provider);
            if (value.Server != null)
                SelectedServer = Servers.FirstOrDefault(s => s.Server == value.Server);
        }
    }

    public string DefaultHint => SelectedDefault?.IsLocal == true
        ? "이 PC에서 처리하므로 문서가 외부로 나가지 않습니다. (AI 요약 창에서 매번 바꿀 수 있음)"
        : "문서 내용이 선택한 AI의 서버로 전송됩니다. 외부로 보내면 안 되는 문서는 이 PC의 서버로 처리하세요.";

    public int TabIndex
    {
        get => _tabIndex;
        set => Set(ref _tabIndex, value);
    }

    static int TabFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => OpenAiTab,
        AiProvider.Gemini => GeminiTab,
        AiProvider.Custom => ServersTab,
        _ => ClaudeTab,
    };

    /// <summary>Rebuilds the default-AI list (server names may have changed) and reselects the same entry.</summary>
    void RefreshDefaults(AiChoice? keep)
    {
        var choices = AiSettings.CloudChoices.Concat(Servers.Select(s => new AiChoice(AiProvider.Custom, s.Server))).ToList();
        _refreshing = true;
        DefaultChoices = choices;
        SelectedDefault = (keep == null ? null : choices.FirstOrDefault(c => c.Matches(keep))) ?? choices[0];
        _refreshing = false;
    }

    // ── Cloud services ──

    public string ClaudeModel { get => _claudeModel; set => Set(ref _claudeModel, value); }
    public string OpenAiModel { get => _openAiModel; set => Set(ref _openAiModel, value); }
    public string GeminiModel { get => _geminiModel; set => Set(ref _geminiModel, value); }

    public IReadOnlyList<string> OpenAiModels { get => _openAiModels; private set => Set(ref _openAiModels, value); }
    public IReadOnlyList<string> GeminiModels { get => _geminiModels; private set => Set(ref _geminiModels, value); }

    /// <summary>Keys are set by the view from its PasswordBoxes.</summary>
    public string ApiKey { get; set; }
    public string OpenAiKey { get; set; }
    public string GeminiKey { get; set; }

    /// <summary>Empty until a model list was loaded; the view then shows it instead of the usual hint.</summary>
    public string OpenAiStatus { get => _openAiStatus; private set => Set(ref _openAiStatus, value); }
    public string GeminiStatus { get => _geminiStatus; private set => Set(ref _geminiStatus, value); }

    Task LoadOpenAiModelsAsync() => LoadModelsAsync(() => _catalog.ListOpenAiModelsAsync(OpenAiKey), OpenAiModel,
        (models, model) => (OpenAiModels, OpenAiModel) = (models, model), status => OpenAiStatus = status);

    Task LoadGeminiModelsAsync() => LoadModelsAsync(() => _catalog.ListGeminiModelsAsync(GeminiKey), GeminiModel,
        (models, model) => (GeminiModels, GeminiModel) = (models, model), status => GeminiStatus = status);

    /// <summary>Fills a model dropdown from the service, keeping the current choice if it is still offered.</summary>
    static async Task LoadModelsAsync(Func<Task<List<string>>> load, string current,
        Action<IReadOnlyList<string>, string> show, Action<string> status)
    {
        status("불러오는 중...");
        try
        {
            var models = await load();
            show(models, models.Contains(current) ? current : models.FirstOrDefault() ?? current);
            status(models.Count > 0
                ? $"연결됨 · 사용할 수 있는 모델 {models.Count}개 (목록에서 고르세요)"
                : "연결은 되었지만 사용할 수 있는 모델이 없습니다.");
        }
        catch (Exception ex)
        {
            status("불러오기 실패: " + ex.Message.Replace(" (파일은 전송되지 않았습니다)", ""));
        }
    }

    // ── Servers ──

    public ObservableCollection<AiServerViewModel> Servers { get; } = new();

    public AiServerViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (Set(ref _selectedServer, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    void AddServerViewModel(AiServer server)
    {
        var vm = new AiServerViewModel(server);
        vm.LabelChanged += () => RefreshDefaults(SelectedDefault);
        Servers.Add(vm);
    }

    void AddServer()
    {
        AddServerViewModel(new AiServer { Name = $"AI 서버 {Servers.Count + 1}" });
        RefreshDefaults(SelectedDefault);
        SelectedServer = Servers[^1];
        ServerAdded?.Invoke();
    }

    void RemoveServer()
    {
        if (_selectedServer is not { } removed)
            return;
        int index = Servers.IndexOf(removed);
        Servers.Remove(removed);
        SelectedServer = Servers.Count > 0 ? Servers[Math.Min(index, Servers.Count - 1)] : null;
        // A removed default falls back to Claude.
        RefreshDefaults(SelectedDefault?.Server == removed.Server ? new AiChoice(AiProvider.Claude) : SelectedDefault);
    }

    async Task LoadServerModelsAsync()
    {
        if (_selectedServer is not { } server)
            return;
        await LoadModelsAsync(() => _catalog.ListServerModelsAsync(server.Server), server.Model,
            (models, model) => (server.ModelChoices, server.Model) = (models, model), status => server.Status = status);
    }

    // ── Save ──

    static string ModelOr(string model, string fallback) => string.IsNullOrWhiteSpace(model) ? fallback : model.Trim();

    /// <summary>Writes the settings; false (after telling the user) if they couldn't be saved.</summary>
    public bool Save()
    {
        foreach (var server in Servers)
        {
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? "새 서버" : server.Name.Trim();
            server.Url = server.Url.Trim();
            server.Model = server.Model.Trim();
        }
        var choice = SelectedDefault ?? new AiChoice(AiProvider.Claude);
        _settings.Provider = choice.Provider;
        _settings.Servers = Servers.Select(s => s.Server).ToList();
        _settings.ServerId = choice.Server?.Id ?? _settings.ServerId;
        _settings.ClaudeModel = ModelOr(ClaudeModel, AiSettings.ClaudeModels[0]);
        _settings.OpenAiModel = ModelOr(OpenAiModel, AiSettings.OpenAiModels[0]);
        _settings.GeminiModel = ModelOr(GeminiModel, AiSettings.GeminiModels[0]);
        _settings.ApiKey = ApiKey;
        _settings.OpenAiKey = OpenAiKey;
        _settings.GeminiKey = GeminiKey;
        try
        {
            _store.Save(_settings);
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.Warn("설정을 저장하지 못했습니다: " + ex.Message, "AI 설정");
            return false;
        }
    }
}
