using System.IO;

namespace FileStar.Services;

/// <summary>Carries out an "이름 한꺼번에 바꾸기" plan.</summary>
public static class BatchRenamer
{
    /// <summary>
    /// Renames in two steps through temporary names, so swaps like a→b, b→a don't trip over each other — on disk
    /// and for the memos, which take the same two steps. Returns the renames done (old path, new path) and a line
    /// per item that couldn't be renamed.
    /// </summary>
    public static (List<(string Old, string New)> Done, List<string> Failed) Apply(IEnumerable<(string Path, string NewName)> plan, INotesStore notes)
    {
        var done = new List<(string, string)>();
        var failed = new List<string>();
        var staged = new List<(string Old, string Temp, string Final)>();
        foreach (var (path, newName) in plan)
        {
            string folder = Path.GetDirectoryName(path)!;
            string temp = Path.Combine(folder, $"~filestar-{Guid.NewGuid():N}");
            try
            {
                DiskPaths.Move(path, temp);
                notes.Rename(path, temp);
                staged.Add((path, temp, Path.Combine(folder, newName)));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        foreach (var (old, temp, final) in staged)
        {
            try
            {
                DiskPaths.Move(temp, final);
                notes.Rename(temp, final);
                done.Add((old, final));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(old)}: {ex.Message}" + PutBack(temp, old, notes));
            }
        }
        return (done, failed);
    }

    /// <summary>Returns an item to its old name; if even that fails, says where it was left.</summary>
    static string PutBack(string temp, string old, INotesStore notes)
    {
        try
        {
            DiskPaths.Move(temp, old);
            notes.Rename(temp, old);
            return "";
        }
        catch (Exception ex)
        {
            return $" (원래 이름으로도 되돌리지 못해 '{Path.GetFileName(temp)}'(으)로 남았습니다: {ex.Message})";
        }
    }
}

/// <summary>"이름 한꺼번에 바꾸기": every name back, through temporary names again (a swap undone is another swap).</summary>
public sealed record BatchRenameUndo(IReadOnlyList<(string Old, string New)> Renames)
    : UndoAction($"이름 한꺼번에 바꾸기 ({Renames.Count:N0}개)")
{
    public override Task<string?> UndoAsync(UndoContext context)
    {
        var back = Renames.Where(r => DiskPaths.Exists(r.New)).Select(r => (r.New, Path.GetFileName(r.Old))).ToList();
        var (_, failed) = BatchRenamer.Apply(back, context.Notes);
        int missing = Renames.Count - back.Count;
        string? problem = failed.Count > 0 ? $"{failed.Count:N0}개는 되돌리지 못했습니다: {string.Join(", ", failed.Take(3))}"
            : missing > 0 ? $"{missing:N0}개는 더 이상 없어서 되돌리지 못했습니다."
            : null;
        return Task.FromResult(problem);
    }
}
