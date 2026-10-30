using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>
/// 검색 탭's panel: the search word, what to look in (이름·확장자·태그·메모) and where (a folder, with or without
/// its subfolders, or every memo and tag). Running it asks the window to show the resulting location.
/// </summary>
public sealed class SearchPanelViewModel : ObservableObject
{
    string _query = "";
    bool _name, _extension, _tag, _memo, _subfolders, _inFolder = true, _isVisible;
    string? _folder;
    bool _syncing;

    public SearchPanelViewModel()
    {
        RunCommand = new RelayCommand(Run);
    }

    /// <summary>The search to show (in the same tab; the previous one stays in 뒤로).</summary>
    public event Action<SearchLocation>? SearchRequested;

    /// <summary>The options can't search anything; tell the user why.</summary>
    public event Action<string>? Problem;

    public ICommand RunCommand { get; }

    /// <summary>The options last used, for the next search tab (saved in the settings).</summary>
    public SearchFields LastFields { get; private set; } = SearchFields.Default;
    public bool LastSubfolders { get; private set; } = true;

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    public string Query
    {
        get => _query;
        set => Set(ref _query, value);
    }

    public bool SearchName { get => _name; set => SetOption(ref _name, value); }
    public bool SearchExtension { get => _extension; set => SetOption(ref _extension, value); }
    public bool SearchTag { get => _tag; set => SetOption(ref _tag, value); }
    public bool SearchMemo { get => _memo; set => SetOption(ref _memo, value); }
    public bool Subfolders { get => _subfolders; set => SetOption(ref _subfolders, value); }

    /// <summary>위치: the folder (true) or every memo and tag (false).</summary>
    public bool InFolder
    {
        get => _inFolder;
        set
        {
            if (SetOption(ref _inFolder, value))
                OnPropertyChanged(nameof(ScopeIndex));
        }
    }

    /// <summary>The 위치 combo box: 0 the folder, 1 모든 위치.</summary>
    public int ScopeIndex
    {
        get => _inFolder ? 0 : 1;
        set => InFolder = value == 0;
    }

    /// <summary>Names, extensions and subfolders only mean something when a folder is searched.</summary>
    public bool FolderOptionsEnabled => _inFolder;

    public string FolderLabel => _folder != null ? "폴더: " + Locations.DisplayName(_folder) : "폴더 (없음)";
    public string? FolderPath => _folder;
    public bool HasFolder => _folder != null;

    public void LoadSettings(string letters) => (LastFields, LastSubfolders) = SearchLocation.ParseOptions(letters);

    public string OptionLetters => SearchLocation.OptionLetters(LastFields, LastSubfolders);

    /// <summary>Shows the panel on search tabs, filled in from the search the tab holds (tag lists have no panel).</summary>
    public void Show(SearchLocation? search)
    {
        IsVisible = search is { Kind: not SearchKind.Tag };
        if (!IsVisible)
            return;
        _syncing = true;
        if (search!.Kind == SearchKind.Here)
            _folder = search.Base;
        Query = search.Query;
        SearchName = search.Fields.HasFlag(SearchFields.Name);
        SearchExtension = search.Fields.HasFlag(SearchFields.Extension);
        SearchTag = search.Fields.HasFlag(SearchFields.Tag);
        SearchMemo = search.Fields.HasFlag(SearchFields.Memo);
        Subfolders = search.Subfolders;
        InFolder = search.Kind == SearchKind.Here;
        OnPropertyChanged(nameof(FolderLabel));
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(HasFolder));
        _syncing = false;
    }

    SearchFields Fields =>
        (_name ? SearchFields.Name : 0) | (_extension ? SearchFields.Extension : 0) |
        (_tag ? SearchFields.Tag : 0) | (_memo ? SearchFields.Memo : 0);

    public void Run()
    {
        bool inFolder = _inFolder && _folder != null;
        var fields = Fields;
        if ((inFolder ? fields : fields & (SearchFields.Tag | SearchFields.Memo)) == SearchFields.None)
        {
            Problem?.Invoke(inFolder ? "찾을 대상을 하나 이상 고르세요." : "모든 위치에서는 태그나 메모에서만 찾을 수 있습니다.");
            return;
        }
        LastFields = fields;
        LastSubfolders = _subfolders;
        SearchRequested?.Invoke(SearchLocation.Find(inFolder ? _folder : null, _query, fields, _subfolders));
    }

    /// <summary>Changing what to look in redoes the search right away (when there is something to look for).</summary>
    bool SetOption(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
            return false;
        if (name == nameof(InFolder))
            OnPropertyChanged(nameof(FolderOptionsEnabled));
        if (!_syncing && _query.Trim().Length > 0)
            Run();
        return true;
    }
}
