using System.Text.Json.Serialization;

namespace FileStar.Models;

/// <param name="FileId">NTFS file ID (see FileIdentity), so the memo can find its file after an outside move.</param>
public sealed record FileNote(string Text, DateTime Updated, List<string>? Tags = null, string? FileId = null)
{
    /// <summary>The first non-empty line, shown in the file list's 메모 column.</summary>
    [JsonIgnore]
    public string Summary => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    [JsonIgnore]
    public IReadOnlyList<string> TagList => Tags ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// The file was changed after this memo was last written or confirmed, so the memo may no longer match it.
    /// Only memo text counts (tags alone aren't a description of the content), and only files (a folder's
    /// time changes whenever anything inside it does).
    /// </summary>
    public bool IsStaleFor(DateTime fileWritten, bool isDirectory) =>
        !isDirectory && Text.Length > 0 && fileWritten > Updated.AddSeconds(2);
}

public static class NoteTags
{
    /// <summary>"#예산, 보고서" → ["예산", "보고서"].</summary>
    public static IEnumerable<string> Parse(string input) => input
        .Split(new[] { ',', ';', '#' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length > 0);
}
