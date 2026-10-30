namespace FileStar.ViewModels;

/// <summary>Compares two folder listings by name: which side has it, and if both, whether it differs.</summary>
public static class FolderCompare
{
    public sealed record Result(int LeftOnly, int RightOnly, int LeftNewer, int RightNewer, int Different, int Same);

    /// <summary>
    /// Marks every item with its <see cref="CompareState"/>. Same = same size and modified time (2 seconds apart
    /// at most: FAT and zip keep times that coarsely). Folders present on both sides count as the same (their
    /// contents are not compared).
    /// </summary>
    public static Result Mark(IReadOnlyList<FileItem> left, IReadOnlyList<FileItem> right)
    {
        var byName = right.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<FileItem>();
        int leftOnly = 0, leftNewer = 0, rightNewer = 0, different = 0, same = 0;
        foreach (var a in left)
        {
            if (!byName.TryGetValue(a.Name, out var b))
            {
                a.CompareState = CompareState.OnlyHere;
                leftOnly++;
                continue;
            }
            matched.Add(b);
            if (a.IsDirectory && b.IsDirectory)
            {
                a.CompareState = b.CompareState = CompareState.Same;
                same++;
                continue;
            }
            if (a.IsDirectory != b.IsDirectory)
            {
                a.CompareState = b.CompareState = CompareState.Different;
                different++;
                continue;
            }
            double seconds = (a.Modified - b.Modified).TotalSeconds;
            if (a.Size == b.Size && Math.Abs(seconds) <= 2)
            {
                a.CompareState = b.CompareState = CompareState.Same;
                same++;
            }
            else if (Math.Abs(seconds) <= 2)
            {
                a.CompareState = b.CompareState = CompareState.Different;   // same time, different size
                different++;
            }
            else if (seconds > 0)
            {
                (a.CompareState, b.CompareState) = (CompareState.Newer, CompareState.Older);
                leftNewer++;
                different++;
            }
            else
            {
                (a.CompareState, b.CompareState) = (CompareState.Older, CompareState.Newer);
                rightNewer++;
                different++;
            }
        }
        int rightOnly = 0;
        foreach (var b in right.Where(b => !matched.Contains(b)))
        {
            b.CompareState = CompareState.OnlyHere;
            rightOnly++;
        }
        return new Result(leftOnly, rightOnly, leftNewer, rightNewer, different, same);
    }

    /// <summary>What one side has that the other lacks or has older: the items 한쪽으로 복사 copies.</summary>
    public static List<FileItem> ToCopy(IEnumerable<FileItem> side) =>
        side.Where(i => i.CompareState is CompareState.OnlyHere or CompareState.Newer).ToList();
}
