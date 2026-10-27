using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace FileStar;

public partial class AiSettingsWindow : Window
{
    readonly AiSettings _settings;
    // Servers are edited on copies so 취소 leaves the saved list untouched.
    readonly ObservableCollection<AiServer> _servers = new();
    AiServer? _editing;
    bool _loading;

    public AiSettingsWindow(AiSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _loading = true;
        ClaudeModelBox.ItemsSource = AiSettings.ClaudeModels;
        ClaudeModelBox.Text = settings.ClaudeModel;
        OpenAiModelBox.ItemsSource = AiSettings.OpenAiModels;
        OpenAiModelBox.Text = settings.OpenAiModel;
        GeminiModelBox.ItemsSource = AiSettings.GeminiModels;
        GeminiModelBox.Text = settings.GeminiModel;
        // Show that keys are stored without revealing them.
        ApiKeyBox.Password = settings.ApiKey;
        OpenAiKeyBox.Password = settings.OpenAiKey;
        GeminiKeyBox.Password = settings.GeminiKey;

        foreach (var server in settings.Servers)
            _servers.Add(server.Clone());
        ServerList.ItemsSource = _servers;
        _loading = false;

        var preferred = settings.DefaultChoice();
        RefreshDefaults(preferred.Provider, preferred.Server?.Id);
        ServerList.SelectedIndex = _servers.Count > 0 ? 0 : -1;
        ShowServer(ServerList.SelectedItem as AiServer);
        Tabs.SelectedItem = TabFor(preferred.Provider);
    }

    TabItem TabFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => OpenAiTab,
        AiProvider.Gemini => GeminiTab,
        AiProvider.Custom => ServersTab,
        _ => ClaudeTab,
    };

    // ───────────── Default AI ─────────────

    /// <summary>Rebuilds the default-AI list (server names may have changed) and reselects the given entry.</summary>
    void RefreshDefaults(AiProvider provider, string? serverId)
    {
        var choices = new List<AiChoice> { new(AiProvider.Claude), new(AiProvider.OpenAI), new(AiProvider.Gemini) };
        choices.AddRange(_servers.Select(s => new AiChoice(AiProvider.Custom, s)));
        bool was = _loading;
        _loading = true;
        DefaultBox.ItemsSource = choices;
        DefaultBox.SelectedItem = choices.FirstOrDefault(c => c.Provider == provider && c.Server?.Id == serverId) ?? choices[0];
        _loading = was;
        UpdateDefaultHint();
    }

    AiChoice SelectedDefault => DefaultBox.SelectedItem as AiChoice ?? new AiChoice(AiProvider.Claude);

    void DefaultBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDefaultHint();
        if (_loading)
            return;
        // Jump to the chosen AI's tab so its settings are right there.
        var choice = SelectedDefault;
        Tabs.SelectedItem = TabFor(choice.Provider);
        if (choice.Server != null)
            ServerList.SelectedItem = choice.Server;
    }

    void UpdateDefaultHint()
    {
        var choice = SelectedDefault;
        DefaultHint.Text = choice.Server is { IsLocal: true }
            ? "이 PC에서 처리하므로 문서가 외부로 나가지 않습니다. (AI 요약 창에서 매번 바꿀 수 있음)"
            : "문서 내용이 선택한 AI의 서버로 전송됩니다. 외부로 보내면 안 되는 문서는 이 PC의 서버로 처리하세요.";
    }

    // ───────────── Cloud model lists ─────────────

    async void LoadOpenAiModels_Click(object sender, RoutedEventArgs e) =>
        await LoadModelsAsync(OpenAiModelBox, OpenAiStatus, () => AiSummarizer.ListOpenAiModelsAsync(OpenAiKeyBox.Password));

    async void LoadGeminiModels_Click(object sender, RoutedEventArgs e) =>
        await LoadModelsAsync(GeminiModelBox, GeminiStatus, () => AiSummarizer.ListGeminiModelsAsync(GeminiKeyBox.Password));

    /// <summary>Fills a model dropdown from the service, keeping the current choice if it is still offered.</summary>
    static async Task LoadModelsAsync(ComboBox box, TextBlock status, Func<Task<List<string>>> load)
    {
        status.Text = "불러오는 중...";
        try
        {
            var models = await load();
            string current = box.Text;
            box.ItemsSource = models;
            box.Text = models.Contains(current) ? current : models.FirstOrDefault() ?? current;
            status.Text = models.Count > 0
                ? $"연결됨 · 사용할 수 있는 모델 {models.Count}개 (목록에서 고르세요)"
                : "연결은 되었지만 사용할 수 있는 모델이 없습니다.";
        }
        catch (Exception ex)
        {
            status.Text = "불러오기 실패: " + ex.Message.Replace(" (파일은 전송되지 않았습니다)", "");
        }
    }

    // ───────────── Servers ─────────────

    void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        Flush();
        ShowServer(ServerList.SelectedItem as AiServer);
    }

    void ShowServer(AiServer? server)
    {
        _loading = true;
        _editing = server;
        ServerEditor.IsEnabled = RemoveServerButton.IsEnabled = server != null;
        ServerNameBox.Text = server?.Name ?? "";
        ServerKindBox.SelectedIndex = server == null ? -1 : (int)server.Kind;
        ServerUrlBox.Text = server?.Url ?? "";
        ServerKeyBox.Password = server?.ApiKey ?? "";
        ServerModelBox.ItemsSource = null;
        ServerModelBox.Text = server?.Model ?? "";
        ServerStatus.Text = server == null ? "'추가'를 눌러 Ollama나 OpenAI 호환 서버를 등록하세요. 여러 개 등록할 수 있습니다." : HintFor(server);
        _loading = false;
    }

    static string HintFor(AiServer server) =>
        (server.Kind == AiServerKind.Ollama
            ? "주소 예: http://localhost:11434 (이 PC), http://192.168.0.20:11434 (다른 PC). 키는 보통 비워 둡니다."
            : "주소 예: http://localhost:1234/v1 (LM Studio), http://서버:8000/v1 (vLLM). 키가 필요한 서버만 입력하세요.")
        + (server.IsLocal ? "" : "\n⚠ 이 PC가 아닌 주소입니다. 문서 내용이 그 서버로 전송됩니다.")
        + "\nPDF는 직접 읽지 못합니다(Claude·ChatGPT·Gemini 사용).";

    /// <summary>Copies the editor fields into the server being edited.</summary>
    void Flush()
    {
        if (_editing == null || _loading)
            return;
        string oldName = _editing.Name;
        bool wasLocal = _editing.IsLocal;
        _editing.Name = string.IsNullOrWhiteSpace(ServerNameBox.Text) ? "새 서버" : ServerNameBox.Text.Trim();
        _editing.Kind = ServerKindBox.SelectedIndex == 1 ? AiServerKind.OpenAiCompatible : AiServerKind.Ollama;
        _editing.Url = ServerUrlBox.Text.Trim();
        _editing.ApiKey = ServerKeyBox.Password;
        // Picking from the dropdown can leave Text a step behind SelectedItem.
        _editing.Model = (ServerModelBox.SelectedItem as string ?? ServerModelBox.Text).Trim();
        if (oldName != _editing.Name || wasLocal != _editing.IsLocal)
        {
            // Names show in the list and in the default-AI box; redraw both without moving the selection.
            _loading = true;
            ServerList.Items.Refresh();
            _loading = false;
            var current = SelectedDefault;
            RefreshDefaults(current.Provider, current.Server?.Id);
        }
    }

    void ServerText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _editing == null)
            return;
        Flush();
        ServerStatus.Text = HintFor(_editing);
    }

    void ServerKey_Changed(object sender, RoutedEventArgs e) => Flush();

    void ServerKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_editing == null || _loading)
            return;
        var kind = ServerKindBox.SelectedIndex == 1 ? AiServerKind.OpenAiCompatible : AiServerKind.Ollama;
        // Swap in the usual address for the new kind if it is still the other kind's default.
        if (kind == AiServerKind.OpenAiCompatible && ServerUrlBox.Text.Trim() == "http://localhost:11434")
            ServerUrlBox.Text = "http://localhost:1234/v1";
        else if (kind == AiServerKind.Ollama && ServerUrlBox.Text.Trim() == "http://localhost:1234/v1")
            ServerUrlBox.Text = "http://localhost:11434";
        Flush();
        ServerStatus.Text = HintFor(_editing);
    }

    void AddServer_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        var server = new AiServer { Name = $"AI 서버 {_servers.Count + 1}" };
        _servers.Add(server);
        var current = SelectedDefault;
        RefreshDefaults(current.Provider, current.Server?.Id);
        ServerList.SelectedItem = server;
        ServerNameBox.Focus();
        ServerNameBox.SelectAll();
    }

    void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not AiServer server)
            return;
        int index = ServerList.SelectedIndex;
        _editing = null;
        _loading = true;
        _servers.Remove(server);
        _loading = false;
        ServerList.SelectedIndex = Math.Min(index, _servers.Count - 1);
        ShowServer(ServerList.SelectedItem as AiServer);
        var current = SelectedDefault;
        // A removed default falls back to Claude.
        RefreshDefaults(current.Server == server ? AiProvider.Claude : current.Provider, current.Server?.Id);
    }

    async void LoadServerModels_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        if (_editing is not { } server)
            return;
        await LoadModelsAsync(ServerModelBox, ServerStatus, () => AiSummarizer.ListServerModelsAsync(server));
        Flush();
    }

    // ───────────── Save ─────────────

    static string ModelOr(ComboBox box, string fallback) => string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        var choice = SelectedDefault;
        _settings.Provider = choice.Provider;
        _settings.Servers = _servers.ToList();
        _settings.ServerId = choice.Server?.Id ?? _settings.ServerId;
        _settings.ClaudeModel = ModelOr(ClaudeModelBox, AiSettings.ClaudeModels[0]);
        _settings.OpenAiModel = ModelOr(OpenAiModelBox, AiSettings.OpenAiModels[0]);
        _settings.GeminiModel = ModelOr(GeminiModelBox, AiSettings.GeminiModels[0]);
        _settings.ApiKey = ApiKeyBox.Password;
        _settings.OpenAiKey = OpenAiKeyBox.Password;
        _settings.GeminiKey = GeminiKeyBox.Password;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "설정을 저장하지 못했습니다: " + ex.Message, "AI 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
