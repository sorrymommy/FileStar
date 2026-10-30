using System.Collections;

namespace FileStar.ViewModels;

/// <summary>Folders first, then by the chosen column; names compare naturally like Explorer.</summary>
public sealed class FileItemComparer(string key, bool descending) : IComparer
{
    /// <summary>The columns a list can be sorted by (the row-number column can't).</summary>
    public static readonly IReadOnlySet<string> SortableKeys = new HashSet<string>
    {
        "Name", "Note", "Tags", "Ext", "Size", "Type", "Folder", "Compare", "Modified", "Created", "Accessed", "Attributes",
    };

    public int Compare(object? x, object? y)
    {
        var a = (FileItem)x!;
        var b = (FileItem)y!;
        if (a.SortGroup != b.SortGroup)
            return a.SortGroup.CompareTo(b.SortGroup);
        if (a.SortOrder != b.SortOrder && key == "Name")
            return descending ? b.SortOrder.CompareTo(a.SortOrder) : a.SortOrder.CompareTo(b.SortOrder);
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        int result = key switch
        {
            // Items with a memo first, then alphabetically by memo.
            "Note" => a.HasNote != b.HasNote
                ? (a.HasNote ? -1 : 1)
                : string.Compare(a.NoteSummary, b.NoteSummary, StringComparison.CurrentCultureIgnoreCase),
            "Tags" => a.Tags.Count != b.Tags.Count && (a.Tags.Count == 0 || b.Tags.Count == 0)
                ? (a.Tags.Count > 0 ? -1 : 1)
                : string.Compare(string.Join(" ", a.Tags), string.Join(" ", b.Tags), StringComparison.CurrentCultureIgnoreCase),
            "Ext" => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            "Size" => Nullable.Compare(a.Size, b.Size),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            "Folder" => Native.StrCmpLogicalW(a.FolderPath, b.FolderPath),
            "Compare" => a.CompareState.CompareTo(b.CompareState),
            "Modified" => a.Modified.CompareTo(b.Modified),
            "Created" => a.Created.CompareTo(b.Created),
            "Accessed" => a.Accessed.CompareTo(b.Accessed),
            "Attributes" => string.CompareOrdinal(a.AttributesText, b.AttributesText),
            _ => 0,
        };
        if (result == 0)
            result = Native.StrCmpLogicalW(a.Name, b.Name);
        return descending ? -result : result;
    }
}
