using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace FileStar.Ai;

/// <summary>Everything one summary needs: the settings (keys, models), the chosen server if any, the document and the tags in use.</summary>
public sealed record AiRequest(AiSettings Settings, AiServer? Server, DocumentContent Document, IReadOnlyCollection<string> KnownTags);

/// <summary>One AI service FileStar can send documents to.</summary>
public interface IAiProvider
{
    AiProvider Kind { get; }

    /// <summary>How much extracted text it is given (large cloud contexts vs. a typical self-hosted model).</summary>
    int MaxChars { get; }

    Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token);
}

public sealed class ClaudeProvider : IAiProvider
{
    // Models that accept the server-side refusal fallback ("default" routing).
    static readonly HashSet<string> FallbackModels = new() { "claude-opus-5-5", "claude-sonnet-5-5" };

    public AiProvider Kind => AiProvider.Claude;

    public int MaxChars => 300_000;

    public async Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token)
    {
        var (settings, _, doc, knownTags) = request;
        // No key in settings → the SDK falls back to ANTHROPIC_API_KEY or an `ant auth login` profile.
        // With none of those, stop here rather than upload the document only to get a 401.
        if (string.IsNullOrEmpty(settings.ApiKey) && !HasAmbientCredentials())
            throw new InvalidOperationException("Claude API 키가 설정되지 않았습니다. 도구 > AI 설정에서 키를 입력하세요. (파일은 전송되지 않았습니다)");
        var client = string.IsNullOrEmpty(settings.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = settings.ApiKey };

        var content = new List<BetaContentBlockParam>();
        if (doc.Pdf != null)
            content.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(doc.Pdf) } });
        if (doc.Image != null)
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(doc.Image), MediaType = MediaType.ImageJpeg },
            });
        content.Add(new BetaTextBlockParam { Text = AiPrompt.User(doc, knownTags) });

        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(AiPrompt.SchemaJson)!;
        var parameters = new MessageCreateParams
        {
            Model = settings.ClaudeModel,
            MaxTokens = 8000,
            System = AiPrompt.System,
            // Summaries don't need deep reasoning; medium keeps cost and latency down.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = schema } },
            Messages = [new BetaUserMessageParam { Content = content }],
        };
        if (FallbackModels.Contains(settings.ClaudeModel))
        {
            // If a safety classifier declines, the API re-serves the request on a suitable model instead of failing.
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, token);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new InvalidOperationException("Claude API 키가 없거나 올바르지 않습니다. 도구 > AI 설정에서 키를 확인하세요.");
        }
        catch (AnthropicNotFoundException)
        {
            throw new InvalidOperationException($"모델 '{settings.ClaudeModel}'을(를) 사용할 수 없습니다. AI 설정에서 다른 모델을 고르세요.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new InvalidOperationException("요청이 너무 많습니다(429). 잠시 후 다시 시도하세요.");
        }
        catch (AnthropicBadRequestException e)
        {
            throw new InvalidOperationException("요청이 거부되었습니다: " + e.Message);
        }
        catch (AnthropicApiException e)
        {
            throw new InvalidOperationException("Claude API 오류: " + e.Message);
        }
        catch (AnthropicIOException e)
        {
            throw new InvalidOperationException("Claude API에 연결할 수 없습니다(네트워크): " + e.Message);
        }

        if (response.StopReason == "refusal")
            throw new InvalidOperationException(AiPrompt.RefusedMessage);
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException(AiPrompt.TruncatedMessage);

        return AiPrompt.Parse(string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)));
    }

    /// <summary>Credentials the SDK would find on its own: environment variables or an `ant auth login` profile.</summary>
    static bool HasAmbientCredentials() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_PROFILE")) ||
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));
}

/// <summary>
/// The OpenAI chat-completions call, shared by ChatGPT and the OpenAI-compatible servers (LM Studio, vLLM …),
/// whose key is optional.
/// </summary>
public static class ChatCompletions
{
    public static async Task<AiResult> SummarizeAsync(string baseUrl, string? key, string model, string service, bool allowPdf,
        DocumentContent doc, IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
        var content = new JsonArray();
        if (doc.Pdf != null && allowPdf)
            content.Add(new JsonObject
            {
                ["type"] = "file",
                ["file"] = new JsonObject
                {
                    ["filename"] = doc.FileName,
                    ["file_data"] = "data:application/pdf;base64," + Convert.ToBase64String(doc.Pdf),
                },
            });
        if (doc.Image != null)
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(doc.Image) },
            });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = AiPrompt.User(doc, knownTags) });

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = AiPrompt.System },
                new JsonObject { ["role"] = "user", ["content"] = content }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "file_memo", ["strict"] = true, ["schema"] = JsonNode.Parse(AiPrompt.SchemaJson) },
            },
        };

        var request = AiHttp.PostJson(baseUrl + "/chat/completions", body);
        AiHttp.SetBearer(request, key);
        var node = await AiHttp.SendJsonAsync(request, service, model, token);

        var choice = node["choices"]?[0];
        var message = choice?["message"];
        if (message?["refusal"]?.GetValue<string>() is { Length: > 0 })
            throw new InvalidOperationException(AiPrompt.RefusedMessage);
        if (choice?["finish_reason"]?.GetValue<string>() == "length")
            throw new InvalidOperationException(AiPrompt.TruncatedMessage);
        return AiPrompt.Parse(message?["content"]?.GetValue<string>()
                              ?? throw new InvalidOperationException($"{service} 응답 형식을 이해할 수 없습니다."));
    }
}

public sealed class OpenAiProvider : IAiProvider
{
    public const string BaseUrl = "https://api.openai.com/v1";

    public AiProvider Kind => AiProvider.OpenAI;

    public int MaxChars => 200_000;

    public static string KeyOrThrow(string key) => AiHttp.KeyOrThrow(key, "OpenAI", "OPENAI_API_KEY");

    public Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token) =>
        ChatCompletions.SummarizeAsync(BaseUrl, KeyOrThrow(request.Settings.OpenAiKey), request.Settings.OpenAiModel,
            "OpenAI", allowPdf: true, request.Document, request.KnownTags, token);
}

public sealed class GeminiProvider : IAiProvider
{
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public AiProvider Kind => AiProvider.Gemini;

    public int MaxChars => 300_000;

    public static string KeyOrThrow(string key) => AiHttp.KeyOrThrow(key, "Gemini", "GEMINI_API_KEY", "GOOGLE_API_KEY");

    public async Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token)
    {
        var (settings, _, doc, knownTags) = request;
        string key = KeyOrThrow(settings.GeminiKey);

        var parts = new JsonArray();
        if (doc.Pdf != null)
            parts.Add(InlineData("application/pdf", doc.Pdf));
        if (doc.Image != null)
            parts.Add(InlineData("image/jpeg", doc.Image));
        parts.Add(new JsonObject { ["text"] = AiPrompt.User(doc, knownTags) });

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = AiPrompt.System }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = JsonNode.Parse(AiPrompt.GeminiSchemaJson),
            },
        };

        string model = settings.GeminiModel.StartsWith("models/") ? settings.GeminiModel[7..] : settings.GeminiModel;
        var httpRequest = AiHttp.PostJson($"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent", body);
        httpRequest.Headers.Add("x-goog-api-key", key);
        var node = await AiHttp.SendJsonAsync(httpRequest, "Gemini", model, token);

        if (node["promptFeedback"]?["blockReason"] != null)
            throw new InvalidOperationException(AiPrompt.RefusedMessage);
        var candidate = node["candidates"]?[0];
        string? finish = candidate?["finishReason"]?.GetValue<string>();
        if (finish == "MAX_TOKENS")
            throw new InvalidOperationException(AiPrompt.TruncatedMessage);
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION")
            throw new InvalidOperationException(AiPrompt.RefusedMessage);
        string json = string.Concat(candidate?["content"]?["parts"]?.AsArray()
            .Select(p => p?["text"]?.GetValue<string>()) ?? Array.Empty<string?>());
        if (json.Length == 0)
            throw new InvalidOperationException("Gemini 응답 형식을 이해할 수 없습니다.");
        return AiPrompt.Parse(json);
    }

    static JsonObject InlineData(string mimeType, byte[] data) =>
        new() { ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = Convert.ToBase64String(data) } };
}

/// <summary>One of the user's own servers: Ollama, or anything speaking the OpenAI chat API.</summary>
public sealed class CustomServerProvider : IAiProvider
{
    public AiProvider Kind => AiProvider.Custom;

    public int MaxChars => 24_000;

    public Task<AiResult> SummarizeAsync(AiRequest request, CancellationToken token)
    {
        var server = request.Server ?? throw new InvalidOperationException("AI 서버가 선택되지 않았습니다.");
        if (string.IsNullOrWhiteSpace(server.Model))
            throw new InvalidOperationException($"'{server.Name}' 서버의 모델이 선택되지 않았습니다. 도구 > AI 설정에서 모델을 고르세요.");
        if (request.Document.Pdf != null)
            throw new InvalidOperationException($"'{server.Name}' 서버는 PDF를 직접 읽을 수 없습니다. PDF는 Claude·ChatGPT·Gemini로 처리하세요.");
        return server.Kind == AiServerKind.Ollama
            ? SummarizeWithOllamaAsync(server, request.Document, request.KnownTags, token)
            : ChatCompletions.SummarizeAsync(OpenAiCompatibleBase(server.Url), server.ApiKey, server.Model, server.Name, allowPdf: false,
                request.Document, request.KnownTags, token);
    }

    /// <summary>"http://host:1234" → "http://host:1234/v1"; an address that already has a path is used as given.</summary>
    public static string OpenAiCompatibleBase(string url)
    {
        url = url.Trim().TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath is "/" or "" ? url + "/v1" : url;
    }

    static async Task<AiResult> SummarizeWithOllamaAsync(AiServer server, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = AiPrompt.User(doc, knownTags) };
        if (doc.Image != null)
            user["images"] = new JsonArray(Convert.ToBase64String(doc.Image));   // needs a vision model (e.g. llava, qwen2.5vl)
        var body = new JsonObject
        {
            ["model"] = server.Model,
            ["stream"] = false,
            ["format"] = JsonNode.Parse(AiPrompt.SchemaJson),
            ["options"] = new JsonObject { ["num_ctx"] = 16384 },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = AiPrompt.System }, user),
        };

        System.Net.Http.HttpResponseMessage http;
        try
        {
            http = await AiHttp.Client.SendAsync(AiHttp.PostJson(server.Url.Trim().TrimEnd('/') + "/api/chat", body), token);
        }
        catch (System.Net.Http.HttpRequestException e)
        {
            throw new InvalidOperationException($"'{server.Name}'({server.Url})에 연결할 수 없습니다. 서버가 실행 중인지 확인하세요. ({e.Message})");
        }
        string text = await http.Content.ReadAsStringAsync(token);
        if (!http.IsSuccessStatusCode)
            throw new InvalidOperationException($"'{server.Name}' 오류 {(int)http.StatusCode}: {text}");
        var message = JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("Ollama 응답 형식을 이해할 수 없습니다.");
        return AiPrompt.Parse(message);
    }
}
