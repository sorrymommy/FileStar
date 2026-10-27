using System.Windows.Input;

namespace FileStar.ViewModels;

/// <summary>폴더 비교: marks every item in both lists against the other, and fills the compare bar.</summary>
public sealed class CompareViewModel : ObservableObject
{
    bool _isOn, _diffOnly;
    string _summary = "";
    FolderCompare.Result? _result;

    /// <summary>Marks or the "다른 것만 보기" filter changed: both lists need refiltering.</summary>
    public event Action? FilterChanged;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value))
                return;
            if (!value)
                DiffOnly = false;
        }
    }

    /// <summary>"다른 것만 보기" hides items that are the same on both sides.</summary>
    public bool DiffOnly
    {
        get => _diffOnly;
        set
        {
            if (Set(ref _diffOnly, value))
                FilterChanged?.Invoke();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool CanCopyRight => _result is { } r && r.LeftOnly + r.LeftNewer > 0;
    public bool CanCopyLeft => _result is { } r && r.RightOnly + r.RightNewer > 0;

    public string CopyRightToolTip => _result is { } r
        ? $"왼쪽에만 있는 {r.LeftOnly:N0}개와 왼쪽이 더 최신인 {r.LeftNewer:N0}개를 오른쪽으로 복사" : "";

    public string CopyLeftToolTip => _result is { } r
        ? $"오른쪽에만 있는 {r.RightOnly:N0}개와 오른쪽이 더 최신인 {r.RightNewer:N0}개를 왼쪽으로 복사" : "";

    public bool Hides(FileItem item) => _isOn && _diffOnly && item.CompareState == CompareState.Same;

    /// <param name="leftFolder">The main list's folder, or null when it shows 내 PC or a result list.</param>
    public void Recompute(string? leftFolder, IReadOnlyList<FileItem> left, string rightFolder, IReadOnlyList<FileItem> right)
    {
        if (!_isOn)
            return;
        if (leftFolder == null || rightFolder.Length == 0)
        {
            SetResult(null, "비교하려면 왼쪽에 실제 폴더를 여세요.");
            return;
        }
        var result = FolderCompare.Mark(left, right);
        SetResult(result, string.Equals(leftFolder, rightFolder, StringComparison.OrdinalIgnoreCase)
            ? "양쪽이 같은 폴더입니다. 오른쪽에서 다른 폴더를 여세요."
            : $"왼쪽에만 {result.LeftOnly:N0} · 오른쪽에만 {result.RightOnly:N0} · 다름 {result.Different:N0} · 같음 {result.Same:N0}");
        FilterChanged?.Invoke();
    }

    /// <summary>Comparing stopped: every mark is cleared.</summary>
    public void Clear(IEnumerable<FileItem> items)
    {
        foreach (var item in items)
            item.CompareState = CompareState.None;
        SetResult(null, "");
        FilterChanged?.Invoke();
    }

    void SetResult(FolderCompare.Result? result, string summary)
    {
        _result = result;
        Summary = summary;
        OnPropertyChanged(nameof(CanCopyRight));
        OnPropertyChanged(nameof(CanCopyLeft));
        OnPropertyChanged(nameof(CopyRightToolTip));
        OnPropertyChanged(nameof(CopyLeftToolTip));
        // The copy buttons' commands ask CanCopyRight/Left; results usually arrive after the input that started them.
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The question asked before copying one side's newer and missing items to the other.</summary>
    public static string CopyQuestion(bool toRight, IReadOnlyList<FileItem> source)
    {
        int replacing = source.Count(i => i.CompareState == CompareState.Newer);
        return $"{(toRight ? "왼쪽 → 오른쪽" : "오른쪽 → 왼쪽")}으로 {source.Count:N0}개를 복사합니다." +
               (replacing > 0 ? $"\n그중 {replacing:N0}개는 반대쪽의 더 오래된 파일을 대신합니다 (오래된 파일은 휴지통으로)." : "") +
               "\n\nCtrl+Z로 한 번에 되돌릴 수 있습니다.";
    }
}
