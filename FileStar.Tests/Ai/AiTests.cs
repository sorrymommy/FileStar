using System.IO;
using FileStar.Tests.Support;

namespace FileStar.Tests.Ai;

public class AiPromptTests
{
    [Fact]
    public void Answers_are_read_with_tags_cleaned_up()
    {
        var result = AiPrompt.Parse("""{"summary": "  요약\n- 항목 ", "tags": ["#예산", "예산", " 보고서 ", ""]}""");
        Assert.Equal("요약\n- 항목", result.Summary);
        Assert.Equal(new[] { "예산", "보고서" }, result.Tags);
    }

    [Fact]
    public void An_answer_that_is_not_json_is_reported() =>
        Assert.Throws<InvalidOperationException>(() => AiPrompt.Parse("죄송합니다"));

    [Fact]
    public void The_prompt_names_the_file_and_the_tags_in_use()
    {
        var doc = new DocumentContent { FileName = "보고서.docx", Text = "본문", Note = "앞부분만" };
        string prompt = AiPrompt.User(doc, new[] { "예산", "회의" });
        Assert.Contains("파일 이름: 보고서.docx", prompt);
        Assert.Contains("기존 태그 목록: 예산, 회의", prompt);
        Assert.Contains("<document>", prompt);
    }

    [Theory]
    [InlineData("http://host:1234", "http://host:1234/v1")]
    [InlineData("http://host:1234/", "http://host:1234/v1")]
    [InlineData("http://host:8000/api/v2", "http://host:8000/api/v2")]
    public void OpenAI_compatible_servers_get_v1_unless_a_path_is_given(string url, string expected) =>
        Assert.Equal(expected, CustomServerProvider.OpenAiCompatibleBase(url));
}

public class AiSummarizerTests
{
    sealed class EchoProvider(AiProvider kind, int maxChars) : IAiProvider
    {
        public AiProvider Kind => kind;
        public int MaxChars => maxChars;
        public Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token) =>
            Task.FromResult(new AiResult($"{kind}:{request.Document.FileName}:{request.Server?.Name}", new()));
    }

    [Fact]
    public async Task Each_choice_goes_to_its_own_provider()
    {
        var summarizer = new AiSummarizer(new IAiProvider[] { new EchoProvider(AiProvider.Claude, 1), new EchoProvider(AiProvider.Custom, 2) });
        var server = new AiServer { Name = "내 서버" };
        var doc = new DocumentContent { FileName = "a.txt" };

        var cloud = await summarizer.SummarizeAsync(new AiSettings(), new AiChoice(AiProvider.Claude), doc, Array.Empty<string>(), default);
        var own = await summarizer.SummarizeAsync(new AiSettings(), new AiChoice(AiProvider.Custom, server), doc, Array.Empty<string>(), default);

        Assert.Equal("Claude:a.txt:", cloud.Summary);
        Assert.Equal("Custom:a.txt:내 서버", own.Summary);
        Assert.Equal(2, summarizer.MaxChars(new AiChoice(AiProvider.Custom, server)));
    }

    [Fact]
    public void A_provider_nobody_registered_is_reported() =>
        Assert.Throws<InvalidOperationException>(() => new AiSummarizer(Array.Empty<IAiProvider>()).MaxChars(new AiChoice(AiProvider.Gemini)));

    [Fact]
    public async Task A_server_without_a_model_is_refused_before_anything_is_sent()
    {
        var request = new AiRequest(new AiSettings(), new AiServer { Name = "빈 서버" }, new DocumentContent { FileName = "a.txt" }, Array.Empty<string>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomServerProvider().SummarizeAsync(request, default));
        Assert.Contains("모델", error.Message);
    }

    [Fact]
    public void Every_built_in_provider_is_registered()
    {
        var summarizer = AiSummarizer.CreateDefault();
        foreach (var kind in Enum.GetValues<AiProvider>())
            Assert.True(summarizer.MaxChars(new AiChoice(kind, new AiServer())) > 0);
    }
}

public sealed class AiSettingsTests : IDisposable
{
    readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Keys_are_written_encrypted_and_read_back()
    {
        var store = new JsonAiSettingsStore(_dir.Combine("ai.json"));
        var settings = new AiSettings { ApiKey = "sk-secret", Provider = AiProvider.Gemini };
        store.Save(settings);

        Assert.DoesNotContain("sk-secret", File.ReadAllText(_dir.Combine("ai.json")));
        var loaded = store.Load();
        Assert.Equal("sk-secret", loaded.ApiKey);
        Assert.Equal(AiProvider.Gemini, loaded.Provider);
    }

    [Fact]
    public void The_old_single_Ollama_setting_becomes_a_server()
    {
        File.WriteAllText(_dir.Combine("ai.json"), """{"Provider": 1, "OllamaUrl": "http://pc:11434", "OllamaModel": "llama"}""");
        var loaded = new JsonAiSettingsStore(_dir.Combine("ai.json")).Load();
        var choice = loaded.DefaultChoice();
        Assert.Equal(AiProvider.Custom, choice.Provider);
        Assert.Equal("http://pc:11434", choice.Server?.Url);
        Assert.Equal("llama", loaded.ModelFor(choice));
        Assert.Null(loaded.OllamaUrl);
    }

    [Fact]
    public void A_removed_default_server_falls_back_to_Claude()
    {
        var settings = new AiSettings { Provider = AiProvider.Custom, Servers = new() };
        Assert.Equal(AiProvider.Claude, settings.DefaultChoice().Provider);
    }

    [Fact]
    public void Remembering_a_choice_reports_whether_anything_changed()
    {
        var settings = new AiSettings();
        Assert.False(settings.Remember(new AiChoice(AiProvider.Claude)));
        Assert.True(settings.Remember(new AiChoice(AiProvider.OpenAI)));
        Assert.Equal(AiProvider.OpenAI, settings.Provider);
    }

    [Fact]
    public void Only_servers_on_this_PC_count_as_private()
    {
        Assert.True(new AiChoice(AiProvider.Custom, new AiServer { Url = "http://localhost:11434" }).IsLocal);
        Assert.False(new AiChoice(AiProvider.Custom, new AiServer { Url = "http://10.0.0.5:11434" }).IsLocal);
        Assert.False(new AiChoice(AiProvider.Claude).IsLocal);
    }
}
