using System.Net.Http;
using System.Text.Json.Nodes;

namespace FileStar.Ai;

/// <summary>Turns one document into a memo (summary) and tags, with whichever AI the user chose.</summary>
public interface IAiSummarizer
{
    /// <summary>How much extracted text the chosen AI is given.</summary>
    int MaxChars(AiChoice choice);

    Task<AiResult> SummarizeAsync(AiSettings settings, AiChoice choice, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token);
}

public sealed class AiSummarizer(IEnumerable<IAiProvider> providers) : IAiSummarizer
{
    readonly Dictionary<AiProvider, IAiProvider> _providers = providers.ToDictionary(p => p.Kind);

    public static AiSummarizer CreateDefault() =>
        new(new IAiProvider[] { new ClaudeProvider(), new OpenAiProvider(), new GeminiProvider(), new CustomServerProvider() });

    IAiProvider For(AiChoice choice) => _providers.TryGetValue(choice.Provider, out var provider)
        ? provider
        : throw new InvalidOperationException($"지원하지 않는 AI입니다: {choice.Label}");

    public int MaxChars(AiChoice choice) => For(choice).MaxChars;

    public Task<AiResult> SummarizeAsync(AiSettings settings, AiChoice choice, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token) =>
        For(choice).SummarizeAsync(new AiRequest(settings, choice.Server, doc, knownTags), token);
}

/// <summary>Which models each service offers this account, for the settings window's dropdowns.</summary>
public interface IAiModelCatalog
{
    Task<List<string>> ListOpenAiModelsAsync(string key, CancellationToken token = default);

    Task<List<string>> ListGeminiModelsAsync(string key, CancellationToken token = default);

    Task<List<string>> ListServerModelsAsync(AiServer server, CancellationToken token = default);
}

public sealed class AiModelCatalog : IAiModelCatalog
{
    static readonly string[] NonChatModelWords = { "audio", "realtime", "tts", "transcribe", "image", "embedding", "search", "moderation", "instruct" };

    public async Task<List<string>> ListOpenAiModelsAsync(string key, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, OpenAiProvider.BaseUrl + "/models");
        AiHttp.SetBearer(request, OpenAiProvider.KeyOrThrow(key));
        var node = await AiHttp.SendJsonAsync(request, "OpenAI", null, token);
        return AiHttp.ModelIds(node)
            .Where(id => (id.StartsWith("gpt-") || id.StartsWith("o")) && !NonChatModelWords.Any(id.Contains))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<List<string>> ListGeminiModelsAsync(string key, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GeminiProvider.BaseUrl + "/models?pageSize=1000");
        request.Headers.Add("x-goog-api-key", GeminiProvider.KeyOrThrow(key));
        var node = await AiHttp.SendJsonAsync(request, "Gemini", null, token);
        return node["models"]?.AsArray()
            .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
            .Select(m => m?["name"]?.GetValue<string>()?.Replace("models/", ""))
            .OfType<string>()
            .Where(name => name.StartsWith("gemini"))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToList() ?? new();
    }

    public async Task<List<string>> ListServerModelsAsync(AiServer server, CancellationToken token = default)
    {
        if (server.Kind == AiServerKind.Ollama)
        {
            string text = await AiHttp.Client.GetStringAsync(server.Url.TrimEnd('/') + "/api/tags", token);
            return JsonNode.Parse(text)?["models"]?.AsArray()
                .Select(m => m?["name"]?.GetValue<string>())
                .OfType<string>()
                .ToList() ?? new();
        }
        var request = new HttpRequestMessage(HttpMethod.Get, CustomServerProvider.OpenAiCompatibleBase(server.Url) + "/models");
        AiHttp.SetBearer(request, server.ApiKey);
        var node = await AiHttp.SendJsonAsync(request, server.Name, null, token);
        return AiHttp.ModelIds(node).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
