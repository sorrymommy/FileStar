using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FileStar.Ai;

// Saved as numbers in ai.json: append new providers, never reorder. Custom (formerly "Ollama") = one of the user's own servers.
public enum AiProvider { Claude, Custom, OpenAI, Gemini }

public enum AiServerKind { Ollama, OpenAiCompatible }

/// <summary>A server the user added: Ollama, or anything speaking the OpenAI chat API (LM Studio, vLLM, a company gateway...).
/// It may run on this PC or elsewhere on the network.</summary>
public sealed class AiServer
{
    public const string OllamaDefaultUrl = "http://localhost:11434";
    public const string OpenAiCompatibleDefaultUrl = "http://localhost:1234/v1";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AiServerKind Kind { get; set; } = AiServerKind.Ollama;
    public string Url { get; set; } = OllamaDefaultUrl;
    public string Model { get; set; } = "";
    public string? EncryptedApiKey { get; set; }

    /// <summary>Optional; most local servers need none.</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => ApiKeyProtector.Unprotect(EncryptedApiKey);
        set => EncryptedApiKey = ApiKeyProtector.Protect(value);
    }

    /// <summary>The address points at this PC.</summary>
    [JsonIgnore]
    public bool HasLocalAddress =>
        Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>One of Ollama's cloud models ("gpt-oss:120b-cloud"): served at a local address but run on ollama.com.</summary>
    [JsonIgnore]
    public bool IsCloudModel =>
        Model.Trim().EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) || Model.Trim().EndsWith(":cloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when documents never leave this PC: a local address and a model that runs here.</summary>
    [JsonIgnore]
    public bool IsLocal => HasLocalAddress && !IsCloudModel;

    [JsonIgnore]
    public string KindLabel => Kind == AiServerKind.Ollama ? "Ollama" : "OpenAI 호환";

    /// <summary>"host:port", for telling the user where documents go.</summary>
    [JsonIgnore]
    public string Host => Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ? uri.Authority : Url;

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

    /// <summary>Where the documents are sent, for the privacy notice.</summary>
    public string Destination => Provider switch
    {
        AiProvider.OpenAI => "OpenAI(ChatGPT) 서버",
        AiProvider.Gemini => "Google(Gemini) 서버",
        AiProvider.Claude => "Anthropic(Claude) 서버",
        _ when Server is { IsCloudModel: true } => $"'{Server.Name}' 서버를 거쳐 Ollama 클라우드(ollama.com)",
        _ => $"'{Server?.Name}' 서버({Server?.Host})",
    };

    public bool IsLocal => Server is { IsLocal: true };

    public bool Matches(AiChoice other) => Provider == other.Provider && Server?.Id == other.Server?.Id;

    public override string ToString() => Label;
}

/// <summary>AI connection settings. API keys are DPAPI-encrypted per Windows user.</summary>
public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public string ClaudeModel { get; set; } = ClaudeModels[0];
    public string? EncryptedApiKey { get; set; }
    public string OpenAiModel { get; set; } = OpenAiModels[0];
    public string? EncryptedOpenAiKey { get; set; }
    public string GeminiModel { get; set; } = GeminiModels[0];
    public string? EncryptedGeminiKey { get; set; }
    public List<AiServer> Servers { get; set; } = new() { new AiServer { Name = "로컬 Ollama" } };
    /// <summary>Which server is the default when <see cref="Provider"/> is Custom.</summary>
    public string? ServerId { get; set; }

    // Older versions kept a single Ollama server here; the store turns it into a Servers entry.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaModel { get; set; }

    public static readonly string[] ClaudeModels = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
    // Starting suggestions only; the settings window can load the account's real list from each service.
    public static readonly string[] OpenAiModels = { "gpt-5.5", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.4-mini" };
    public static readonly string[] GeminiModels = { "gemini-3.6-flash", "gemini-2.5-pro", "gemini-2.5-flash" };

    /// <summary>Claude key. Empty means "use ANTHROPIC_API_KEY or the `ant auth login` profile".</summary>
    [JsonIgnore]   // only the DPAPI-encrypted copy is written to disk
    public string ApiKey
    {
        get => ApiKeyProtector.Unprotect(EncryptedApiKey);
        set => EncryptedApiKey = ApiKeyProtector.Protect(value);
    }

    /// <summary>Empty means "use OPENAI_API_KEY".</summary>
    [JsonIgnore]
    public string OpenAiKey
    {
        get => ApiKeyProtector.Unprotect(EncryptedOpenAiKey);
        set => EncryptedOpenAiKey = ApiKeyProtector.Protect(value);
    }

    /// <summary>Empty means "use GEMINI_API_KEY or GOOGLE_API_KEY".</summary>
    [JsonIgnore]
    public string GeminiKey
    {
        get => ApiKeyProtector.Unprotect(EncryptedGeminiKey);
        set => EncryptedGeminiKey = ApiKeyProtector.Protect(value);
    }

    public static IEnumerable<AiChoice> CloudChoices =>
        new[] { new AiChoice(AiProvider.Claude), new AiChoice(AiProvider.OpenAI), new AiChoice(AiProvider.Gemini) };

    /// <summary>Every AI the user can pick: the three cloud services, then each of their servers.</summary>
    public List<AiChoice> Choices() => CloudChoices.Concat(Servers.Select(s => new AiChoice(AiProvider.Custom, s))).ToList();

    /// <summary>The saved default, falling back to Claude if its server was removed.</summary>
    public AiChoice DefaultChoice()
    {
        if (Provider != AiProvider.Custom)
            return new(Provider);
        var server = Servers.FirstOrDefault(s => s.Id == ServerId) ?? Servers.FirstOrDefault();
        return server != null ? new(AiProvider.Custom, server) : new(AiProvider.Claude);
    }

    /// <summary>Makes <paramref name="choice"/> the default; false if it already was.</summary>
    public bool Remember(AiChoice choice)
    {
        if (Provider == choice.Provider && (choice.Server == null || ServerId == choice.Server.Id))
            return false;
        Provider = choice.Provider;
        if (choice.Server != null)
            ServerId = choice.Server.Id;
        return true;
    }

    /// <summary>Model name shown next to the choice.</summary>
    public string ModelFor(AiChoice choice) => choice.Provider switch
    {
        AiProvider.Claude => ClaudeModel,
        AiProvider.OpenAI => OpenAiModel,
        AiProvider.Gemini => GeminiModel,
        _ => choice.Server?.Model ?? "",
    };
}

/// <summary>Encrypts API keys for the current Windows user (DPAPI), so ai.json never holds them in plain text.</summary>
public static class ApiKeyProtector
{
    public static string Unprotect(string? encrypted)
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

    public static string? Protect(string value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
}

public interface IAiSettingsStore
{
    AiSettings Load();

    /// <summary>Throws if the file can't be written.</summary>
    void Save(AiSettings settings);
}

/// <summary>AI settings in %AppData%\FileStar\ai.json.</summary>
public sealed class JsonAiSettingsStore(string filePath) : IAiSettingsStore
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public AiSettings Load()
    {
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<AiSettings>(json) ?? new();
                // Older versions also wrote the keys in plain text next to the encrypted copy; rewrite without them.
                bool hadPlainKeys = JsonNode.Parse(json) is JsonObject raw &&
                                    (raw.ContainsKey(nameof(AiSettings.ApiKey)) || raw.ContainsKey(nameof(AiSettings.OpenAiKey)) ||
                                     raw.ContainsKey(nameof(AiSettings.GeminiKey)));
                if (settings.OllamaUrl != null)
                {
                    var server = new AiServer { Name = "로컬 Ollama", Url = settings.OllamaUrl, Model = settings.OllamaModel ?? "" };
                    settings.Servers = new() { server };
                    settings.ServerId = server.Id;
                    settings.OllamaUrl = settings.OllamaModel = null;
                }
                if (hadPlainKeys)
                    Save(settings);
                return settings;
            }
        }
        catch
        {
            // Unreadable: keep a copy, since the next save (e.g. remembering the AI picked) would replace it with defaults.
            try { File.Copy(filePath, filePath + ".corrupt", overwrite: true); }
            catch { }
        }
        return new();
    }

    public void Save(AiSettings settings) =>
        DiskPaths.WriteAllTextAtomically(filePath, JsonSerializer.Serialize(settings, WriteOptions));
}
