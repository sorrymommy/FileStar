using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace FileStar;

// Saved as numbers in ai.json: append new providers, never reorder. Custom (formerly "Ollama") = one of the user's own servers.
public enum AiProvider { Claude, Custom, OpenAI, Gemini }

public enum AiServerKind { Ollama, OpenAiCompatible }

/// <summary>A server the user added: Ollama, or anything speaking the OpenAI chat API (LM Studio, vLLM, a company gateway...).
/// It may run on this PC or elsewhere on the network.</summary>
public sealed class AiServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AiServerKind Kind { get; set; } = AiServerKind.Ollama;
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "";
    public string? EncryptedApiKey { get; set; }

    /// <summary>Optional; most local servers need none.</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => AiSettings.Unprotect(EncryptedApiKey);
        set => EncryptedApiKey = AiSettings.Protect(value);
    }

    /// <summary>True when the address points at this PC, so documents never leave it.</summary>
    [JsonIgnore]
    public bool IsLocal =>
        Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string KindLabel => Kind == AiServerKind.Ollama ? "Ollama" : "OpenAI 호환";

    public AiServer Clone() => (AiServer)MemberwiseClone();

    public override string ToString() => Name;   // what screen readers and UI Automation see in lists
}

/// <summary>What one AI run uses: a cloud provider, or one of the user's servers.</summary>
public sealed record AiChoice(AiProvider Provider, AiServer? Server = null)
{
    public string Label => Provider switch
    {
        AiProvider.Claude => "Claude (Anthropic)",
        AiProvider.OpenAI => "ChatGPT (OpenAI)",
        AiProvider.Gemini => "Gemini (Google)",
        _ => $"{Server?.Name} ({Server?.KindLabel}{(Server?.IsLocal == true ? ", 이 PC" : "")})",
    };

    public override string ToString() => Label;
}

/// <summary>AI connection settings, saved to %AppData%\FileStar\ai.json. API keys are DPAPI-encrypted per Windows user.</summary>
public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public string? EncryptedApiKey { get; set; }
    public string OpenAiModel { get; set; } = "gpt-5.5";
    public string? EncryptedOpenAiKey { get; set; }
    public string GeminiModel { get; set; } = "gemini-3.6-flash";
    public string? EncryptedGeminiKey { get; set; }
    public List<AiServer> Servers { get; set; } = new() { new AiServer { Name = "로컬 Ollama" } };
    /// <summary>Which server is the default when <see cref="Provider"/> is Custom.</summary>
    public string? ServerId { get; set; }

    // Older versions kept a single Ollama server here; Load() turns it into a Servers entry.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaModel { get; set; }

    public static readonly string[] ClaudeModels = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
    // Starting suggestions only; the settings window can load the account's real list from each service.
    public static readonly string[] OpenAiModels = { "gpt-5.5", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.4-mini" };
    public static readonly string[] GeminiModels = { "gemini-3.6-flash", "gemini-2.5-pro", "gemini-2.5-flash" };

    static string FilePath => AppPaths.DataFile("ai.json");

    /// <summary>Claude key. Empty means "use ANTHROPIC_API_KEY or the `ant auth login` profile".</summary>
    [JsonIgnore]   // only the DPAPI-encrypted copy is written to disk
    public string ApiKey
    {
        get => Unprotect(EncryptedApiKey);
        set => EncryptedApiKey = Protect(value);
    }

    /// <summary>Empty means "use OPENAI_API_KEY".</summary>
    [JsonIgnore]   // only the DPAPI-encrypted copy is written to disk
    public string OpenAiKey
    {
        get => Unprotect(EncryptedOpenAiKey);
        set => EncryptedOpenAiKey = Protect(value);
    }

    /// <summary>Empty means "use GEMINI_API_KEY or GOOGLE_API_KEY".</summary>
    [JsonIgnore]   // only the DPAPI-encrypted copy is written to disk
    public string GeminiKey
    {
        get => Unprotect(EncryptedGeminiKey);
        set => EncryptedGeminiKey = Protect(value);
    }

    /// <summary>Every AI the user can pick: the three cloud services, then each of their servers.</summary>
    public List<AiChoice> Choices() =>
        new List<AiChoice> { new(AiProvider.Claude), new(AiProvider.OpenAI), new(AiProvider.Gemini) }
            .Concat(Servers.Select(s => new AiChoice(AiProvider.Custom, s)))
            .ToList();

    /// <summary>The saved default, falling back to Claude if its server was removed.</summary>
    public AiChoice DefaultChoice()
    {
        if (Provider != AiProvider.Custom)
            return new(Provider);
        var server = Servers.FirstOrDefault(s => s.Id == ServerId) ?? Servers.FirstOrDefault();
        return server != null ? new(AiProvider.Custom, server) : new(AiProvider.Claude);
    }

    /// <summary>Model name shown next to the choice.</summary>
    public string ModelFor(AiChoice choice) => choice.Provider switch
    {
        AiProvider.Claude => ClaudeModel,
        AiProvider.OpenAI => OpenAiModel,
        AiProvider.Gemini => GeminiModel,
        _ => choice.Server?.Model ?? "",
    };

    internal static string Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    internal static string? Protect(string value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));

    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AiSettings>(json) ?? new();
                // Older versions also wrote the keys in plain text next to the encrypted copy; rewrite without them.
                bool hadPlainKeys = JsonNode.Parse(json) is JsonObject raw &&
                                    (raw.ContainsKey(nameof(ApiKey)) || raw.ContainsKey(nameof(OpenAiKey)) || raw.ContainsKey(nameof(GeminiKey)));
                if (settings.OllamaUrl != null)
                {
                    var server = new AiServer { Name = "로컬 Ollama", Url = settings.OllamaUrl, Model = settings.OllamaModel ?? "" };
                    settings.Servers = new() { server };
                    settings.ServerId = server.Id;
                    settings.OllamaUrl = settings.OllamaModel = null;
                }
                if (hadPlainKeys)
                    settings.Save();
                return settings;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record AiResult(string Summary, List<string> Tags);

/// <summary>Turns one document into a memo (summary) and tags, with a cloud AI or one of the user's servers.</summary>
public static class AiSummarizer
{
    /// <summary>How much extracted text each provider is given (large cloud contexts vs. a typical self-hosted model).</summary>
    public static int MaxChars(AiChoice choice) => choice.Provider switch
    {
        AiProvider.Claude or AiProvider.Gemini => 300_000,
        AiProvider.OpenAI => 200_000,
        _ => 24_000,
    };

    const string SystemPrompt = """
        당신은 파일 탐색기의 '파일 메모' 작성 도우미입니다. 사용자는 파일을 열어 보지 않고도 무엇이 들어 있는지
        알아보고, 나중에 다시 찾기 위해 파일마다 메모와 태그를 남깁니다. 문서의 종류는 업무 문서, 개인 기록,
        사진, 코드 등 무엇이든 될 수 있습니다.

        주어진 문서를 읽고 한국어로 다음을 작성하세요.
        - summary: 첫 줄은 이 파일이 무엇인지 알 수 있는 한 줄 요약(40자 안팎). 둘째 줄부터는 '- '로 시작하는 핵심 내용
          3~6개. 날짜, 금액, 수량, 사람·조직·장소 이름, 결론처럼 나중에 찾을 때 쓸모 있는 구체적인 정보를 우선하고,
          알 수 있으면 쪽·시트·슬라이드 위치를 덧붙이세요.
        - tags: 이 파일을 다시 찾을 때 쓸 핵심 키워드 3~6개. 띄어쓰기 없는 짧은 명사로, '#'은 붙이지 마세요.
          기존 태그 목록에 같은 뜻의 태그가 있으면 그 표기를 그대로 쓰세요.

        문서에 없는 내용은 추측하지 마세요. 내용이 거의 없거나 읽을 수 없으면 summary에 그렇다고 쓰세요.
        """;

    static readonly string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "첫 줄 한 줄 요약 + '- ' 항목들 (줄바꿈으로 구분)" },
            "tags": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "tags"],
          "additionalProperties": false
        }
        """;

    static string UserPrompt(DocumentContent doc, IReadOnlyCollection<string> knownTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"파일 이름: {doc.FileName}");
        if (doc.Note != null)
            sb.AppendLine($"참고: {doc.Note}");
        sb.AppendLine("기존 태그 목록: " + (knownTags.Count > 0 ? string.Join(", ", knownTags.Take(200)) : "(없음)"));
        if (doc.Text != null)
        {
            sb.AppendLine();
            sb.AppendLine("<document>");
            sb.AppendLine(doc.Text);
            sb.AppendLine("</document>");
        }
        sb.AppendLine();
        sb.Append("이 파일의 메모(summary)와 태그(tags)를 작성하세요.");
        return sb.ToString();
    }

    public static Task<AiResult> SummarizeAsync(AiSettings settings, AiChoice choice, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token) => choice.Provider switch
        {
            AiProvider.Claude => SummarizeWithClaudeAsync(settings, doc, knownTags, token),
            AiProvider.OpenAI => ChatCompletionsAsync(OpenAiBase, OpenAiKeyOrThrow(settings.OpenAiKey), settings.OpenAiModel,
                                                      "OpenAI", allowPdf: true, doc, knownTags, token),
            AiProvider.Gemini => SummarizeWithGeminiAsync(settings, doc, knownTags, token),
            _ => SummarizeWithServerAsync(choice.Server ?? throw new InvalidOperationException("AI 서버가 선택되지 않았습니다."),
                                          doc, knownTags, token),
        };

    static Task<AiResult> SummarizeWithServerAsync(AiServer server, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(server.Model))
            throw new InvalidOperationException($"'{server.Name}' 서버의 모델이 선택되지 않았습니다. 도구 > AI 설정에서 모델을 고르세요.");
        if (doc.Pdf != null)
            throw new InvalidOperationException($"'{server.Name}' 서버는 PDF를 직접 읽을 수 없습니다. PDF는 Claude·ChatGPT·Gemini로 처리하세요.");
        return server.Kind == AiServerKind.Ollama
            ? SummarizeWithOllamaAsync(server, doc, knownTags, token)
            : ChatCompletionsAsync(OpenAiCompatibleBase(server.Url), server.ApiKey, server.Model, server.Name, allowPdf: false, doc, knownTags, token);
    }

    /// <summary>"http://host:1234" → "http://host:1234/v1"; an address that already has a path is used as given.</summary>
    static string OpenAiCompatibleBase(string url)
    {
        url = url.Trim().TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath is "/" or "" ? url + "/v1" : url;
    }

    // ───────────── Claude ─────────────

    // Models that accept the server-side refusal fallback ("default" routing).
    static readonly HashSet<string> FallbackModels = new() { "claude-opus-5-5", "claude-sonnet-5-5" };

    static async Task<AiResult> SummarizeWithClaudeAsync(AiSettings settings, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
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
        content.Add(new BetaTextBlockParam { Text = UserPrompt(doc, knownTags) });

        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;
        var parameters = new MessageCreateParams
        {
            Model = settings.ClaudeModel,
            MaxTokens = 8000,
            System = SystemPrompt,
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
            throw new InvalidOperationException("AI가 이 문서의 처리를 거절했습니다.");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("응답이 길이 제한에 걸려 잘렸습니다.");

        string json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return Parse(json);
    }

    /// <summary>Credentials the SDK would find on its own: environment variables or an `ant auth login` profile.</summary>
    static bool HasAmbientCredentials() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_PROFILE")) ||
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ───────────── OpenAI (ChatGPT) ─────────────

    const string OpenAiBase = "https://api.openai.com/v1";

    static string OpenAiKeyOrThrow(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI API 키가 설정되지 않았습니다. 도구 > AI 설정에서 키를 입력하세요. (파일은 전송되지 않았습니다)");
        return key.Trim();
    }

    /// <summary>The OpenAI chat-completions call, shared by ChatGPT and OpenAI-compatible servers (key optional for those).</summary>
    static async Task<AiResult> ChatCompletionsAsync(string baseUrl, string? key, string model, string service, bool allowPdf,
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
        content.Add(new JsonObject { ["type"] = "text", ["text"] = UserPrompt(doc, knownTags) });

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = content }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "file_memo", ["strict"] = true, ["schema"] = JsonNode.Parse(SchemaJson) },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
        var node = await SendJsonAsync(request, service, model, token);

        var choice = node["choices"]?[0];
        var message = choice?["message"];
        if (message?["refusal"]?.GetValue<string>() is { Length: > 0 })
            throw new InvalidOperationException("AI가 이 문서의 처리를 거절했습니다.");
        if (choice?["finish_reason"]?.GetValue<string>() == "length")
            throw new InvalidOperationException("응답이 길이 제한에 걸려 잘렸습니다.");
        return Parse(message?["content"]?.GetValue<string>()
                     ?? throw new InvalidOperationException($"{service} 응답 형식을 이해할 수 없습니다."));
    }

    /// <summary>Models a user's server offers, for the settings dropdown.</summary>
    public static async Task<List<string>> ListServerModelsAsync(AiServer server, CancellationToken token = default)
    {
        if (server.Kind == AiServerKind.Ollama)
            return await ListOllamaModelsAsync(server.Url);
        var request = new HttpRequestMessage(HttpMethod.Get, OpenAiCompatibleBase(server.Url) + "/models");
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.ApiKey);
        var node = await SendJsonAsync(request, server.Name, null, token);
        return node["data"]?.AsArray()
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new();
    }

    /// <summary>Chat models available to this OpenAI key, for the settings dropdown.</summary>
    public static async Task<List<string>> ListOpenAiModelsAsync(string key, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, OpenAiBase + "/models");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OpenAiKeyOrThrow(key));
        var node = await SendJsonAsync(request, "OpenAI", null, token);
        string[] skip = { "audio", "realtime", "tts", "transcribe", "image", "embedding", "search", "moderation", "instruct" };
        return node["data"]?.AsArray()
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .Where(id => (id.StartsWith("gpt-") || id.StartsWith("o")) && !skip.Any(id.Contains))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .ToList() ?? new();
    }

    // ───────────── Google Gemini ─────────────

    const string GeminiBase = "https://generativelanguage.googleapis.com/v1beta";

    static string GeminiKeyOrThrow(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Gemini API 키가 설정되지 않았습니다. 도구 > AI 설정에서 키를 입력하세요. (파일은 전송되지 않았습니다)");
        return key.Trim();
    }

    // Gemini's responseSchema is an OpenAPI subset (no additionalProperties), so it gets its own copy.
    const string GeminiSchemaJson = """
        {
          "type": "OBJECT",
          "properties": {
            "summary": { "type": "STRING", "description": "첫 줄 한 줄 요약 + '- ' 항목들 (줄바꿈으로 구분)" },
            "tags": { "type": "ARRAY", "items": { "type": "STRING" } }
          },
          "required": ["summary", "tags"]
        }
        """;

    static async Task<AiResult> SummarizeWithGeminiAsync(AiSettings settings, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
        string key = GeminiKeyOrThrow(settings.GeminiKey);

        var parts = new JsonArray();
        if (doc.Pdf != null)
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "application/pdf", ["data"] = Convert.ToBase64String(doc.Pdf) } });
        if (doc.Image != null)
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(doc.Image) } });
        parts.Add(new JsonObject { ["text"] = UserPrompt(doc, knownTags) });

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = JsonNode.Parse(GeminiSchemaJson),
            },
        };

        string model = settings.GeminiModel.StartsWith("models/") ? settings.GeminiModel[7..] : settings.GeminiModel;
        var request = new HttpRequestMessage(HttpMethod.Post, $"{GeminiBase}/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", key);
        var node = await SendJsonAsync(request, "Gemini", model, token);

        if (node["promptFeedback"]?["blockReason"] != null)
            throw new InvalidOperationException("AI가 이 문서의 처리를 거절했습니다.");
        var candidate = node["candidates"]?[0];
        string? finish = candidate?["finishReason"]?.GetValue<string>();
        if (finish == "MAX_TOKENS")
            throw new InvalidOperationException("응답이 길이 제한에 걸려 잘렸습니다.");
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION")
            throw new InvalidOperationException("AI가 이 문서의 처리를 거절했습니다.");
        string json = string.Concat(candidate?["content"]?["parts"]?.AsArray()
            .Select(p => p?["text"]?.GetValue<string>()) ?? Array.Empty<string?>());
        if (json.Length == 0)
            throw new InvalidOperationException("Gemini 응답 형식을 이해할 수 없습니다.");
        return Parse(json);
    }

    /// <summary>Gemini models this key can call generateContent on, for the settings dropdown.</summary>
    public static async Task<List<string>> ListGeminiModelsAsync(string key, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GeminiBase + "/models?pageSize=1000");
        request.Headers.Add("x-goog-api-key", GeminiKeyOrThrow(key));
        var node = await SendJsonAsync(request, "Gemini", null, token);
        return node["models"]?.AsArray()
            .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
            .Select(m => m?["name"]?.GetValue<string>()?.Replace("models/", ""))
            .OfType<string>()
            .Where(name => name.StartsWith("gemini"))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToList() ?? new();
    }

    /// <summary>Sends a request to a cloud API and turns HTTP failures into the same plain messages the Claude path uses.</summary>
    static async Task<JsonNode> SendJsonAsync(HttpRequestMessage request, string service, string? model, CancellationToken token)
    {
        HttpResponseMessage http;
        try
        {
            http = await Http.SendAsync(request, token);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"{service} API에 연결할 수 없습니다(네트워크): " + e.Message);
        }
        string text = await http.Content.ReadAsStringAsync(token);
        if (!http.IsSuccessStatusCode)
        {
            string detail = text;
            try
            {
                detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException) { }
            if (detail.Length > 300)
                detail = detail[..300] + "…";
            throw new InvalidOperationException((int)http.StatusCode switch
            {
                401 or 403 => $"{service} API 키가 없거나 올바르지 않습니다. 도구 > AI 설정에서 키를 확인하세요. ({detail})",
                404 when model != null => $"모델 '{model}'을(를) 사용할 수 없습니다. AI 설정에서 '모델 목록'을 불러와 다른 모델을 고르세요.",
                429 => $"요청이 너무 많거나 사용 한도를 넘었습니다(429). 잠시 후 다시 시도하세요. ({detail})",
                _ => $"{service} API 오류 {(int)http.StatusCode}: {detail}",
            });
        }
        try
        {
            return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{service} 응답이 비어 있습니다.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{service} 응답 형식을 이해할 수 없습니다.");
        }
    }

    // ───────────── Ollama server (this PC or the network) ─────────────

    static async Task<AiResult> SummarizeWithOllamaAsync(AiServer server, DocumentContent doc,
        IReadOnlyCollection<string> knownTags, CancellationToken token)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = UserPrompt(doc, knownTags) };
        if (doc.Image != null)
            user["images"] = new JsonArray(Convert.ToBase64String(doc.Image));   // needs a vision model (e.g. llava, qwen2.5vl)
        var body = new JsonObject
        {
            ["model"] = server.Model,
            ["stream"] = false,
            ["format"] = JsonNode.Parse(SchemaJson),
            ["options"] = new JsonObject { ["num_ctx"] = 16384 },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt }, user),
        };

        HttpResponseMessage http;
        try
        {
            http = await Http.PostAsync(server.Url.Trim().TrimEnd('/') + "/api/chat",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), token);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"'{server.Name}'({server.Url})에 연결할 수 없습니다. 서버가 실행 중인지 확인하세요. ({e.Message})");
        }
        string text = await http.Content.ReadAsStringAsync(token);
        if (!http.IsSuccessStatusCode)
            throw new InvalidOperationException($"'{server.Name}' 오류 {(int)http.StatusCode}: {text}");
        var message = JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("Ollama 응답 형식을 이해할 수 없습니다.");
        return Parse(message);
    }

    /// <summary>Installed Ollama models, for the settings dropdown.</summary>
    public static async Task<List<string>> ListOllamaModelsAsync(string url)
    {
        string text = await Http.GetStringAsync(url.TrimEnd('/') + "/api/tags");
        return JsonNode.Parse(text)?["models"]?.AsArray()
            .Select(m => m?["name"]?.GetValue<string>())
            .OfType<string>()
            .ToList() ?? new();
    }

    static AiResult Parse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)!;
            string summary = node["summary"]?.GetValue<string>()?.Trim() ?? "";
            var tags = node["tags"]?.AsArray()
                .Select(t => t?.GetValue<string>()?.Trim().TrimStart('#'))
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? new();
            return new AiResult(summary, tags);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("AI 응답을 해석할 수 없습니다: " + (json.Length > 200 ? json[..200] + "…" : json));
        }
    }
}
