using System.Windows.Media;

namespace FileStar.ViewModels;

/// <summary>A tab: the location it shows and its own back/forward history.</summary>
public sealed class BrowserTab(string path) : ObservableObject
{
    string _path = path;
    bool _isActive, _isPinned;

    public string FullPath
    {
        get => _path;
        set
        {
            if (Set(ref _path, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    public string Title => Locations.DisplayName(_path);
    public bool IsThisPc => Locations.IsThisPc(_path);

    /// <summary>The tab shows a list rather than a real folder (내 PC, search or tag results).</summary>
    public bool IsVirtual => Locations.IsVirtual(_path);

    /// <summary>The search or tag list the tab shows, if any.</summary>
    public SearchLocation? Search => SearchLocation.TryParse(_path);

    public override string ToString() => Title;   // what screen readers and UI Automation read for the tab

    public ImageSource? Icon => IsThisPc ? ShellInfo.ThisPcIcon
        : Search is { } search ? (search.Kind == SearchKind.Tag ? ShellInfo.TagIcon : ShellInfo.SearchIcon)
        : ShellInfo.GetIcon(_path, true, Locations.IsRoot(_path));

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Pinned (고정) tabs can't be closed until they are unpinned.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();

    /// <summary>Goes to <paramref name="path"/>, remembering where the tab was.</summary>
    public void Visit(string path)
    {
        Back.Push(FullPath);
        Forward.Clear();
        FullPath = path;
    }

    /// <summary>One step back; returns the location left, or null if there is no history.</summary>
    public string? StepBack()
    {
        if (Back.Count == 0)
            return null;
        string previous = FullPath;
        Forward.Push(previous);
        FullPath = Back.Pop();
        return previous;
    }

    public string? StepForward()
    {
        if (Forward.Count == 0)
            return null;
        string previous = FullPath;
        Back.Push(previous);
        FullPath = Forward.Pop();
        return previous;
    }
}
