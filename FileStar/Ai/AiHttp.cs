using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileStar.Ai;

/// <summary>The HTTP side shared by the REST providers: one client, and HTTP failures turned into plain messages.</summary>
public static class AiHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static HttpRequestMessage PostJson(string url, JsonNode body) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    public static void SetBearer(HttpRequestMessage request, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    /// <summary>A key from the settings, else from the environment; throws (before anything is sent) when there is none.</summary>
    public static string KeyOrThrow(string key, string service, params string[] environmentVariables)
    {
        if (string.IsNullOrWhiteSpace(key))
            key = environmentVariables.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"{service} API 키가 설정되지 않았습니다. 도구 > AI 설정에서 키를 입력하세요. (파일은 전송되지 않았습니다)");
        return key.Trim();
    }

    /// <summary>Sends a request to a cloud API and turns HTTP failures into the same plain messages the Claude path uses.</summary>
    public static async Task<JsonNode> SendJsonAsync(HttpRequestMessage request, string service, string? model, CancellationToken token)
    {
        HttpResponseMessage http;
        try
        {
            http = await Client.SendAsync(request, token);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"{service} API에 연결할 수 없습니다(네트워크): " + e.Message);
        }
        string text = await http.Content.ReadAsStringAsync(token);
        if (!http.IsSuccessStatusCode)
            throw new InvalidOperationException(FailureMessage((int)http.StatusCode, ErrorDetail(text), service, model));
        try
        {
            return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{service} 응답이 비어 있습니다.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{service} 응답 형식을 이해할 수 없습니다.");
        }
    }

    static string ErrorDetail(string body)
    {
        string detail = body;
        try
        {
            detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body;
        }
        catch (JsonException) { }
        return detail.Length > 300 ? detail[..300] + "…" : detail;
    }

    static string FailureMessage(int status, string detail, string service, string? model) => status switch
    {
        401 or 403 => $"{service} API 키가 없거나 올바르지 않습니다. 도구 > AI 설정에서 키를 확인하세요. ({detail})",
        404 when model != null => $"모델 '{model}'을(를) 사용할 수 없습니다. AI 설정에서 '모델 목록'을 불러와 다른 모델을 고르세요.",
        429 => $"요청이 너무 많거나 사용 한도를 넘었습니다(429). 잠시 후 다시 시도하세요. ({detail})",
        _ => $"{service} API 오류 {status}: {detail}",
    };

    /// <summary>The ids in an OpenAI-style model list ({"data": [{"id": …}]}).</summary>
    public static IEnumerable<string> ModelIds(JsonNode node) =>
        node["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>() ?? Enumerable.Empty<string>();
}
