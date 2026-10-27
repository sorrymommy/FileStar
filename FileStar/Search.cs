using System.IO;
using System.Text.RegularExpressions;

namespace FileStar;

public enum SearchKind
{
    /// <summary>Everything in a folder (and, optionally, its subfolders).</summary>
    Here,
    /// <summary>Every memo and tag, wherever the file is.</summary>
    Notes,
    /// <summary>Files carrying one tag (the tree's 태그 branch).</summary>
    Tag,
}

/// <summary>What a search looks at (the search panel's 찾을 대상 checkboxes).</summary>
[Flags]
public enum SearchFields
{
    None = 0,
    Name = 1,
    Extension = 2,
    Tag = 4,
    Memo = 8,
    /// <summary>Name, tags and memos: the default.</summary>
    Default = Name | Tag | Memo,
}

/// <summary>
/// A search shown as a location of its own, so it lives in a tab, has back/forward and refreshes with F5 like
/// a folder. Encoded in the tab's path as "search:kind|base|~options|query" (options: n 이름, e 확장자, t 태그,
/// m 메모, s 하위 폴더 포함); tabs saved before the options existed read as "search:kind|base|query".
/// </summary>
public sealed record SearchLocation(SearchKind Kind, string Base, string Query, SearchFields Fields = SearchFields.Default, bool Subfolders = true)
{
    const string Prefix = "search:";

    public string Path => Kind == SearchKind.Tag
        ? $"{Prefix}tag||{Query}"
        : $"{Prefix}{Kind.ToString().ToLowerInvariant()}|{Base}|~{OptionLetters(Fields, Subfolders)}|{Query}";

    public static string OptionLetters(SearchFields fields, bool subfolders) =>
        (fields.HasFlag(SearchFields.Name) ? "n" : "") + (fields.HasFlag(SearchFields.Extension) ? "e" : "") +
        (fields.HasFlag(SearchFields.Tag) ? "t" : "") + (fields.HasFlag(SearchFields.Memo) ? "m" : "") + (subfolders ? "s" : "");

    public static (SearchFields Fields, bool Subfolders) ParseOptions(string letters)
    {
        var fields = SearchFields.None;
        if (letters.Contains('n')) fields |= SearchFields.Name;
        if (letters.Contains('e')) fields |= SearchFields.Extension;
        if (letters.Contains('t')) fields |= SearchFields.Tag;
        if (letters.Contains('m')) fields |= SearchFields.Memo;
        return (fields, letters.Contains('s'));
    }

    public static SearchLocation? TryParse(string? path)
    {
        if (path == null || !path.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = path[Prefix.Length..].Split('|', 3);
        if (parts.Length != 3 || !Enum.TryParse<SearchKind>(parts[0], ignoreCase: true, out var kind))
            return null;
        if (kind == SearchKind.Tag || !parts[2].StartsWith('~'))
            return new SearchLocation(kind, parts[1], parts[2], kind == SearchKind.Notes ? SearchFields.Tag | SearchFields.Memo : SearchFields.Default);
        var rest = parts[2].Split('|', 2);
        var (fields, subfolders) = ParseOptions(rest[0][1..]);
        return new SearchLocation(kind, parts[1], rest.Length > 1 ? rest[1] : "", fields, subfolders);
    }

    /// <summary>A search of <paramref name="folder"/>, or of every memo and tag when it is null.</summary>
    public static SearchLocation Find(string? folder, string query, SearchFields fields, bool subfolders) => folder == null
        ? new(SearchKind.Notes, "", query.Trim(), fields & (SearchFields.Tag | SearchFields.Memo), true)
        : new(SearchKind.Here, folder, query.Trim(), fields, subfolders);

    public static SearchLocation Tag(string tag) => new(SearchKind.Tag, "", tag);

    /// <summary>Tab title and address-bar text.</summary>
    public string Title => Kind switch
    {
        SearchKind.Tag => $"#{Query}",
        _ when Query.Length == 0 => Kind == SearchKind.Here ? $"검색 — {FileSystem.DisplayName(Base)}" : "메모·태그 검색",
        SearchKind.Here => $"'{Query}' 검색 — {FileSystem.DisplayName(Base)}",
        _ => $"'{Query}' 메모·태그 검색",
    };

    /// <summary>"이름·태그·메모".</summary>
    public string FieldNames => string.Join("·", new[]
    {
        Fields.HasFlag(SearchFields.Name) ? "이름" : null,
        Fields.HasFlag(SearchFields.Extension) ? "확장자" : null,
        Fields.HasFlag(SearchFields.Tag) ? "태그" : null,
        Fields.HasFlag(SearchFields.Memo) ? "메모" : null,
    }.OfType<string>());

    public string Description => Kind switch
    {
        SearchKind.Tag => $"태그 #{Query}가 붙은 모든 파일·폴더",
        _ when Query.Length == 0 => "검색어를 입력하고 Enter를 누르세요.",
        SearchKind.Here => $"{Base}{(Subfolders ? " 와 그 하위 폴더" : "")}에서 {FieldNames}에 '{Query}'가 들어간 항목",
        _ => $"모든 폴더의 {FieldNames}에서 '{Query}'를 찾은 결과",
    };
}

/// <summary>Runs a <see cref="SearchLocation"/> (off the UI thread), handing results over in batches as they are found.</summary>
public static class SearchRunner
{
    /// <summary>Enough to be useful; a search that matches more should be narrowed down.</summary>
    public const int MaxResults = 5000;

    /// <summary>
    /// "보고서; *.pptx; #예산": terms are separated by ; or , and any one matching is enough. Wildcards match the
    /// whole name, plain words match anywhere, #words match tags only. For 확장자, "pdf", ".pdf" and "*.pdf" all
    /// mean the extension pdf.
    /// </summary>
    sealed record Matcher(SearchFields Fields, Regex? Text, List<Regex> Extensions, List<string> Tags)
    {
        public bool Item(string path, bool isDirectory)
        {
            string name = Path.GetFileName(path);
            if (Fields.HasFlag(SearchFields.Name) && Text != null && Text.IsMatch(name))
                return true;
            if (Fields.HasFlag(SearchFields.Extension) && !isDirectory && Extensions.Count > 0)
            {
                string ext = Path.GetExtension(name).TrimStart('.');
                return ext.Length > 0 && Extensions.Any(e => e.IsMatch(ext));
            }
            return false;
        }

        public bool Note(FileNote note) =>
            Tags.Count > 0 && note.TagList.Any(t => Tags.Any(f => t.Contains(f, StringComparison.CurrentCultureIgnoreCase))) ||
            Text != null && (Fields.HasFlag(SearchFields.Memo) && Text.IsMatch(note.Text) ||
                             Fields.HasFlag(SearchFields.Tag) && note.TagList.Any(Text.IsMatch));

        /// <summary>Names or extensions are searched, so the folders have to be walked.</summary>
        public bool LooksAtItems => Text != null && Fields.HasFlag(SearchFields.Name) || Extensions.Count > 0 && Fields.HasFlag(SearchFields.Extension);
    }

    static Matcher Parse(string query, SearchFields fields)
    {
        var parts = query.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tags = parts.Where(p => p.StartsWith('#')).Select(p => p.TrimStart('#').Trim()).Where(p => p.Length > 0).ToList();
        var terms = parts.Where(p => !p.StartsWith('#')).ToList();
        var text = terms.Count == 0 ? null : new Regex(string.Join("|", terms.Select(p => p.Contains('*') || p.Contains('?') ? Wildcard(p) : Regex.Escape(p))), Options);
        var extensions = terms
            .Select(p => p.TrimStart('*').TrimStart('.'))
            .Where(p => p.Length > 0)
            .Select(p => new Regex(Wildcard(p), Options))
            .ToList();
        return new Matcher(fields, text, extensions, tags);
    }

    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static string Wildcard(string pattern) => "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";

    /// <param name="notes">A snapshot of every memo (NotesStore is UI-thread only).</param>
    /// <param name="found">Called (on this background thread) with each batch of new results.</param>
    /// <returns>True if the search stopped at <see cref="MaxResults"/>.</returns>
    public static bool Run(SearchLocation search, List<(string Path, FileNote Note)> notes, FileAttributes skip,
        Action<List<string>> found, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<string>();
        bool Add(string path)
        {
            if (!seen.Add(path))
                return seen.Count < MaxResults;
            batch.Add(path);
            if (batch.Count >= 100)
            {
                found(batch);
                batch = new List<string>();
            }
            return seen.Count < MaxResults;
        }

        bool full = false;
        if (search.Kind == SearchKind.Tag)
        {
            foreach (var (path, note) in notes)
            {
                if (note.TagList.Any(t => string.Equals(t, search.Query, StringComparison.CurrentCultureIgnoreCase)) && Exists(path) && !Add(path))
                {
                    full = true;
                    break;
                }
            }
        }
        else if (search.Query.Length > 0)
        {
            var matcher = Parse(search.Query, search.Fields);
            string folder = search.Base.TrimEnd('\\');
            bool Inside(string path) => search.Kind != SearchKind.Here ||
                (search.Subfolders
                    ? path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase));
            // Memos and tags first: instant, and usually what the user is after.
            foreach (var (path, note) in notes)
            {
                token.ThrowIfCancellationRequested();
                if (Inside(path) && matcher.Note(note) && Exists(path) && !Add(path))
                {
                    full = true;
                    break;
                }
            }
            // Then names and extensions, walking the folder (and its subfolders).
            if (!full && search.Kind == SearchKind.Here && matcher.LooksAtItems && Directory.Exists(search.Base))
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = search.Subfolders, AttributesToSkip = skip };
                foreach (var info in new DirectoryInfo(search.Base).EnumerateFileSystemInfos("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (matcher.Item(info.FullName, info is DirectoryInfo) && !Add(info.FullName))
                    {
                        full = true;
                        break;
                    }
                }
            }
        }
        if (batch.Count > 0)
            found(batch);
        return full;
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
