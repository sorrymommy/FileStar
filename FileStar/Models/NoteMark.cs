namespace FileStar.Models;

/// <summary>The marker shown before an item's icon in the list and tree.</summary>
public enum NoteMark
{
    None,
    /// <summary>The item has memo text.</summary>
    Memo,
    /// <summary>Tags but no memo text.</summary>
    TagsOnly,
    /// <summary>A folder with memos or tags somewhere inside it.</summary>
    InsideFolder,
    /// <summary>The file changed after its memo was written: the memo may be out of date.</summary>
    Stale,
}

/// <summary>폴더 비교: how an item relates to the other list.</summary>
public enum CompareState
{
    None,
    OnlyHere,
    Newer,
    Older,
    Different,
    Same,
}
