using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileStar.Ai;

public sealed record AiResult(string Summary, List<string> Tags);

/// <summary>What every provider asks for, and how its answer is read: the same prompt and JSON shape for all.</summary>
public static class AiPrompt
{
    public const string System = """
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

    /// <summary>JSON Schema for the answer (Claude, OpenAI and Ollama).</summary>
    public const string SchemaJson = """
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

    /// <summary>Gemini's responseSchema is an OpenAPI subset (no additionalProperties), so it gets its own copy.</summary>
    public const string GeminiSchemaJson = """
        {
          "type": "OBJECT",
          "properties": {
            "summary": { "type": "STRING", "description": "첫 줄 한 줄 요약 + '- ' 항목들 (줄바꿈으로 구분)" },
            "tags": { "type": "ARRAY", "items": { "type": "STRING" } }
          },
          "required": ["summary", "tags"]
        }
        """;

    public const string RefusedMessage = "AI가 이 문서의 처리를 거절했습니다.";
    public const string TruncatedMessage = "응답이 길이 제한에 걸려 잘렸습니다.";

    public static string User(DocumentContent doc, IReadOnlyCollection<string> knownTags)
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

    /// <summary>Reads {"summary": …, "tags": […]}; tags lose a leading '#' and duplicates.</summary>
    public static AiResult Parse(string json)
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
