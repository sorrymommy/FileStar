using System.IO;

namespace FileStar.ViewModels;

/// <summary>"이름 한꺼번에 바꾸기": find/replace or a numbering pattern, with a live preview of every new name.</summary>
public sealed class BatchRenameViewModel : ObservableObject
{
    readonly IReadOnlyList<FileItem> _items;
    readonly Func<string, bool> _exists;
    bool _replaceMode = true, _includeExtension;
    string _find = "", _replace = "", _pattern = "{name}_{n}", _start = "001";
    IReadOnlyList<BatchRenameRow> _rows = Array.Empty<BatchRenameRow>();

    /// <param name="exists">Whether a path is taken on disk (a file system lookup; replaceable in tests).</param>
    public BatchRenameViewModel(IReadOnlyList<FileItem> items, Func<string, bool>? exists = null)
    {
        _items = items;
        _exists = exists ?? DiskPaths.Exists;
        Update();
    }

    public string Title => $"이름 한꺼번에 바꾸기 — {_items.Count:N0}개";

    public bool ReplaceMode
    {
        get => _replaceMode;
        set
        {
            if (Set(ref _replaceMode, value))
            {
                OnPropertyChanged(nameof(PatternMode));
                Update();
            }
        }
    }

    public bool PatternMode
    {
        get => !_replaceMode;
        set => ReplaceMode = !value;
    }

    public string Find { get => _find; set => SetAndUpdate(ref _find, value); }
    public string Replace { get => _replace; set => SetAndUpdate(ref _replace, value); }
    public string Pattern { get => _pattern; set => SetAndUpdate(ref _pattern, value); }
    public string Start { get => _start; set => SetAndUpdate(ref _start, value); }

    public bool IncludeExtension
    {
        get => _includeExtension;
        set
        {
            if (Set(ref _includeExtension, value))
                Update();
        }
    }

    public IReadOnlyList<BatchRenameRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public int ChangingCount => _rows.Count(r => r.WillRename);

    public string ApplyText => $"{ChangingCount:N0}개 바꾸기";

    public string ProblemText => _rows.Count(r => r.Problem != null) is > 0 and var problems ? $"문제가 있는 {problems:N0}개는 건너뜁니다." : "";

    /// <summary>(old full path, new name) for every item that will be renamed.</summary>
    public List<(string Path, string NewName)> Plan() => _rows.Where(r => r.WillRename).Select(r => (r.Path, r.NewName)).ToList();

    void SetAndUpdate(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name))
            Update();
    }

    void Update()
    {
        Rows = BuildRows();
        OnPropertyChanged(nameof(ChangingCount));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(ProblemText));
    }

    List<BatchRenameRow> BuildRows()
    {
        var rows = new List<BatchRenameRow>();
        string startText = _start.Trim();
        int number = int.TryParse(startText, out int s) ? s : 1;
        int digits = startText.Length > 1 && startText.StartsWith('0') ? startText.Length : 1;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = new HashSet<string>(_items.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            bool keepExt = !_includeExtension && !item.IsDirectory;
            string ext = keepExt ? item.RawExtension : "";
            string stem = keepExt ? Path.GetFileNameWithoutExtension(item.Name) : item.Name;
            string newStem = _replaceMode
                ? (_find.Length == 0 ? stem : stem.Replace(_find, _replace, StringComparison.CurrentCultureIgnoreCase))
                : _pattern
                    .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                    .Replace("{n}", number.ToString(new string('0', digits)), StringComparison.OrdinalIgnoreCase)
                    .Replace("{date}", item.Modified.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase);
            number++;
            string newName = (newStem + ext).Trim();
            string target = Path.Combine(item.FolderPath, newName);
            string? problem = null;
            if (newName.Length == 0)
                problem = "빈 이름";
            else if (FileNames.HasInvalidChars(newName))
                problem = "쓸 수 없는 문자";
            else if (!taken.Add(target))
                problem = "이름이 겹침";
            else if (!string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase) && _exists(target) && !renamed.Contains(target))
                problem = "같은 이름이 이미 있음";
            rows.Add(new BatchRenameRow(item.FullPath, item.Name, newName, newName != item.Name, problem));
        }
        return rows;
    }
}

public sealed record BatchRenameRow(string Path, string Old, string NewName, bool Changed, string? Problem)
{
    public bool WillRename => Changed && Problem == null;

    /// <summary>The preview's 새 이름 column.</summary>
    public string New => Problem == null ? NewName : $"{NewName}   ⚠ {Problem}";
}
