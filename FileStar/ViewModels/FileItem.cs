using System.IO;
using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>A row in the file list.</summary>
public sealed class FileItem : ObservableObject
{
    FileNote? _note;
    bool _isRenaming, _containsNotes;
    CompareState _compareState;
    ImageSource? _icon;
    bool _iconLoaded;
    string? _typeName;

    /// <summary>The name cell is showing an edit box (F2).</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    /// <summary>The user's memo about this file, if any.</summary>
    public FileNote? Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
                OnPropertyChanged(nameof(NoteSummary));
                OnPropertyChanged(nameof(NoteText));
                OnPropertyChanged(nameof(Tags));
                OnPropertyChanged(nameof(MarkKind));
            }
        }
    }

    /// <summary>A folder with memos or tags somewhere inside it.</summary>
    public bool ContainsNotes
    {
        get => _containsNotes;
        set
        {
            if (Set(ref _containsNotes, value))
                OnPropertyChanged(nameof(MarkKind));
        }
    }

    /// <summary>폴더 비교 against the other list (None = not comparing).</summary>
    public CompareState CompareState
    {
        get => _compareState;
        set
        {
            if (Set(ref _compareState, value))
                OnPropertyChanged(nameof(CompareText));
        }
    }

    public string CompareText => _compareState switch
    {
        CompareState.OnlyHere => "이쪽에만",
        CompareState.Newer => "더 최신",
        CompareState.Older => "더 오래됨",
        CompareState.Different => "다름",
        CompareState.Same => "같음",
        _ => "",
    };

    /// <summary>Changed after its memo was written: the memo may be out of date (shown as a red memo marker).</summary>
    public bool NoteStale => _note?.IsStaleFor(Modified, IsDirectory) == true;

    public NoteMark MarkKind => NoteStale ? NoteMark.Stale
        : HasNote ? NoteMark.Memo
        : Tags.Count > 0 ? NoteMark.TagsOnly
        : ContainsNotes ? NoteMark.InsideFolder
        : NoteMark.None;

    /// <summary>True when there is memo text (tags alone don't show the ✎ mark).</summary>
    public bool HasNote => !string.IsNullOrEmpty(_note?.Text);
    public string NoteSummary => _note?.Summary ?? "";
    public string? NoteText => string.IsNullOrEmpty(_note?.Text) ? null : _note.Text;
    public IReadOnlyList<string> Tags => _note?.TagList ?? Array.Empty<string>();

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }

    // ── Used by the 내 PC view: folders before drives, in a fixed order, with real icons and descriptive types ──
    public int SortGroup { get; init; }
    public int SortOrder { get; init; }
    public bool ExactIcon { get; init; }
    public string? TypeOverride { init => _typeName = value; }

    /// <summary>A drive in the 내 PC view.</summary>
    public bool IsDrive => SortGroup == 1;

    /// <summary>A copy shown under a friendly name in 내 PC (e.g. "문서" for C:\Users\me\Documents).</summary>
    public FileItem ForThisPc(string name, int group, int order, string typeName) => new()
    {
        Name = name,
        FullPath = FullPath,
        IsDirectory = IsDirectory,
        Modified = Modified,
        Created = Created,
        Accessed = Accessed,
        Attributes = Attributes,
        SortGroup = group,
        SortOrder = order,
        ExactIcon = true,
        TypeOverride = typeName,
    };

    public bool IsHidden { get; init; }
    public long? Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public DateTime Accessed { get; init; }
    public FileAttributes Attributes { get; init; }

    /// <summary>Explorer-style attribute letters: R(읽기 전용) H(숨김) S(시스템) A(보관) C(압축) E(암호화).</summary>
    public string AttributesText =>
        (Attributes.HasFlag(FileAttributes.ReadOnly) ? "R" : "") +
        (Attributes.HasFlag(FileAttributes.Hidden) ? "H" : "") +
        (Attributes.HasFlag(FileAttributes.System) ? "S" : "") +
        (Attributes.HasFlag(FileAttributes.Archive) ? "A" : "") +
        (Attributes.HasFlag(FileAttributes.Compressed) ? "C" : "") +
        (Attributes.HasFlag(FileAttributes.Encrypted) ? "E" : "");

    /// <summary>Extension with the dot, e.g. ".pptx".</summary>
    public string RawExtension { get; init; } = "";

    public string Extension { get; init; } = "";

    public bool IsZip => !IsDirectory && Extension == "zip";

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _icon = ShellInfo.GetIcon(FullPath, IsDirectory, ExactIcon);
                _iconLoaded = true;
            }
            return _icon;
        }
    }

    public string TypeName => _typeName ??= ShellInfo.GetTypeName(RawExtension, IsDirectory);

    /// <summary>The folder it is in (the 위치 column of search results).</summary>
    public string FolderPath => Path.GetDirectoryName(FullPath.TrimEnd('\\')) ?? "";
    public string SizeText => Size is long size ? SizeFormat.KB(size) : "";
    public string ModifiedText => FormatDate(Modified);
    public string CreatedText => FormatDate(Created);
    public string AccessedText => FormatDate(Accessed);

    /// <summary>The info pane's one-line description: type, size and date.</summary>
    public string Description => IsDrive ? TypeName
        : IsDirectory ? $"파일 폴더 · 수정 {ModifiedText}"
        : $"{TypeName} · {SizeFormat.Bytes(Size ?? 0)} · 수정 {ModifiedText}";

    /// <summary>What <see cref="ShellInfo.PrefetchAsync"/> needs to warm up this row's icon and type name.</summary>
    public (string Path, bool IsDirectory, bool ExactIcon, string Extension) ShellKey => (FullPath, IsDirectory, ExactIcon, RawExtension);

    static string FormatDate(DateTime time) => time == default ? "" : time.ToString("yyyy-MM-dd tt h:mm:ss");

    public override string ToString() => Name;   // what screen readers and UI Automation read for the row

    public static FileItem? From(FileSystemInfo info)
    {
        try
        {
            bool isDirectory = info is DirectoryInfo;
            string ext = isDirectory ? "" : Path.GetExtension(info.Name);
            return new FileItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDirectory,
                IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                Size = info is FileInfo file ? file.Length : null,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
                Accessed = info.LastAccessTime,
                Attributes = info.Attributes,
                RawExtension = ext,
                Extension = ext.TrimStart('.').ToLowerInvariant(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A row for a path found by a search (file or folder).</summary>
    public static FileItem? FromPath(string path) =>
        From(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path));

    /// <summary>Fills in the memo columns and markers from <paramref name="notes"/>.</summary>
    public void ApplyNotes(INotesStore notes)
    {
        Note = notes.Get(FullPath);
        if (IsDirectory)
            ContainsNotes = notes.FolderHasNotes(FullPath);
    }
}
