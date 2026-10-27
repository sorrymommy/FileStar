using System.IO;
using System.Text.RegularExpressions;

namespace FileStar.Services;

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

    // NonBacktracking: a query like "*a*a*a*a*b" against a long memo stays linear instead of backtracking for minutes.
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    static string Wildcard(string pattern) => "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";

    /// <param name="notes">A snapshot of every memo (the notes store is UI-thread only).</param>
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
                if (note.TagList.Any(t => string.Equals(t, search.Query, StringComparison.CurrentCultureIgnoreCase)) && DiskPaths.Exists(path) && !Add(path))
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
                if (Inside(path) && matcher.Note(note) && DiskPaths.Exists(path) && !Add(path))
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
}
