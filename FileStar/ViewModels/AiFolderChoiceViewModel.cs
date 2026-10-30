using System.IO;

namespace FileStar.ViewModels;

/// <summary>
/// When folders are picked for AI: what was found inside them, whether to include subfolders, and whether to skip
/// files that already have a memo (unless the file changed after it).
/// </summary>
public sealed class AiFolderChoiceViewModel : ObservableObject
{
    readonly AiFolderScan _scan;
    readonly INotesStore _notes;
    bool _withSubfolders, _skipNoted = true;

    public AiFolderChoiceViewModel(AiFolderScan scan, INotesStore notes)
    {
        _scan = scan;
        _notes = notes;
        _withSubfolders = scan.Direct.Count == 0;
    }

    public string Intro => $"선택한 폴더 {_scan.FolderCount:N0}개에서 AI가 읽을 수 있는 파일을 찾았습니다." +
                           "\n(문서·한글·PDF·이미지·텍스트 파일만 포함, 프로그램·압축 파일 등은 제외)";

    public string DirectLabel => $"폴더 바로 안의 파일만 ({_scan.Direct.Count:N0}개)";

    public string AllLabel => $"하위 폴더까지 모두 ({_scan.All.Count:N0}개{(_scan.Truncated ? "+, 최대 " + AiFileCollector.MaxFiles.ToString("N0") + "개까지만" : "")})";

    public bool CanIncludeSubfolders => _scan.All.Count > _scan.Direct.Count;

    public bool DirectOnly
    {
        get => !_withSubfolders;
        set => WithSubfolders = !value;
    }

    public bool WithSubfolders
    {
        get => _withSubfolders;
        set
        {
            if (!Set(ref _withSubfolders, value))
                return;
            OnPropertyChanged(nameof(DirectOnly));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool SkipNoted
    {
        get => _skipNoted;
        set
        {
            if (Set(ref _skipNoted, value))
                OnPropertyChanged(nameof(SummaryText));
        }
    }

    public List<string> Chosen => (_withSubfolders ? _scan.All : _scan.Direct)
        .Where(path => !_skipNoted || _notes.Get(path) is not { Text.Length: > 0 } note ||
                       note.IsStaleFor(File.GetLastWriteTime(path), isDirectory: false))
        .ToList();

    public string SummaryText => Chosen.Count is var count && count == 0
        ? "처리할 파일이 없습니다."
        : $"{count:N0}개 파일을 AI 요약 목록에 올립니다. 시작 전에 목록과 사용할 AI를 확인할 수 있습니다." +
          (count > 100 ? "\n파일이 많으면 시간이 오래 걸리고, 클라우드 AI는 요금도 그만큼 나옵니다." : "");
}
