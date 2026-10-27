using System.IO;
using System.Security.Principal;
using System.Text;

namespace FileStar;

/// <summary>One file operation done in FileStar that Ctrl+Z can take back.</summary>
public abstract record UndoAction(string Description)
{
    /// <summary>Reverses the operation; returns a short message about anything that couldn't be put back.</summary>
    public abstract Task<string?> UndoAsync(IntPtr owner);
}

/// <summary>Rename (in the list or the tree): give the old name back.</summary>
public sealed record RenameUndo(string OldPath, string NewPath)
    : UndoAction($"이름 바꾸기 ({Path.GetFileName(NewPath)} → {Path.GetFileName(OldPath)})")
{
    public override Task<string?> UndoAsync(IntPtr owner)
    {
        if (!File.Exists(NewPath) && !Directory.Exists(NewPath))
            return Task.FromResult<string?>($"'{Path.GetFileName(NewPath)}'이(가) 더 이상 없습니다.");
        if (Directory.Exists(NewPath))
            Directory.Move(NewPath, OldPath);
        else
            File.Move(NewPath, OldPath);
        NotesStore.Rename(NewPath, OldPath);
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Move (paste after cut, drag): move everything back to where it came from.</summary>
public sealed record MoveUndo(IReadOnlyList<(string Source, string Target)> Items)
    : UndoAction(Items.Count == 1 ? $"이동 ({Path.GetFileName(Items[0].Target)})" : $"이동 ({Items.Count:N0}개)")
{
    public override async Task<string?> UndoAsync(IntPtr owner)
    {
        int missing = 0;
        foreach (var (source, target) in Items)
        {
            if (!File.Exists(target) && !Directory.Exists(target) || File.Exists(source) || Directory.Exists(source))
            {
                missing++;
                continue;
            }
            if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                // Same drive: a plain rename back, instant and exact (also restores a name changed on arrival).
                if (Directory.Exists(target))
                    Directory.Move(target, source);
                else
                    File.Move(target, source);
                NotesStore.Rename(target, source);
            }
            else
            {
                // Across drives Windows copies and deletes; let it do that with its own progress window.
                string folder = Path.GetDirectoryName(source)!;
                await FileOps.TransferAsync(owner, new[] { target }, folder, move: true, record: false);
                string arrived = Path.Combine(folder, Path.GetFileName(target));
                if (!string.Equals(arrived, source, StringComparison.OrdinalIgnoreCase) && (File.Exists(arrived) || Directory.Exists(arrived)))
                {
                    if (Directory.Exists(arrived))
                        Directory.Move(arrived, source);
                    else
                        File.Move(arrived, source);
                    NotesStore.Rename(arrived, source);
                }
            }
        }
        return missing > 0 ? $"{missing:N0}개는 이미 옮겨졌거나 지워져서 되돌리지 못했습니다." : null;
    }
}

/// <summary>Copy, new folder, zip, extract: remove what was created (to the Recycle Bin, so nothing is lost for good).</summary>
public sealed record CreateUndo(IReadOnlyList<string> Created, string What)
    : UndoAction(Created.Count == 1 ? $"{What} ({Path.GetFileName(Created[0])})" : $"{What} ({Created.Count:N0}개)")
{
    public override async Task<string?> UndoAsync(IntPtr owner)
    {
        var existing = Created.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count > 0)
        {
            await FileOps.DeleteAsync(owner, existing, permanently: false, noConfirm: true, record: false);
            NotesStore.RemoveTrees(existing.Where(p => !File.Exists(p) && !Directory.Exists(p)));
        }
        return existing.Count < Created.Count ? "일부는 이미 지워져 있었습니다." : null;
    }
}

/// <summary>
/// Delete to the Recycle Bin: take the items back out of it, to their original places. <paramref name="From"/>/
/// <paramref name="To"/> bracket the deletion, so a later deletion of the same path (e.g. undoing a copy that
/// replaced it) isn't restored by mistake.
/// </summary>
public sealed record DeleteUndo(IReadOnlyList<string> Paths, DateTime From, DateTime To)
    : UndoAction(Paths.Count == 1 ? $"삭제 ({Path.GetFileName(Paths[0])})" : $"삭제 ({Paths.Count:N0}개)")
{
    public override Task<string?> UndoAsync(IntPtr owner)
    {
        int failed = Paths.Count(path => !RecycleBin.Restore(path, From, To));
        return Task.FromResult(failed > 0 ? $"{failed:N0}개는 휴지통에서 찾지 못해 복원하지 못했습니다." : null);
    }
}

/// <summary>Several operations undone as one (e.g. renaming many files at once).</summary>
public sealed record GroupUndo(IReadOnlyList<UndoAction> Actions, string Label) : UndoAction(Label)
{
    public override async Task<string?> UndoAsync(IntPtr owner)
    {
        var problems = new List<string>();
        foreach (var action in Actions.Reverse())
        {
            if (await action.UndoAsync(owner) is string problem)
                problems.Add(problem);
        }
        return problems.Count > 0 ? string.Join(" ", problems.Distinct()) : null;
    }
}

/// <summary>The operations Ctrl+Z can take back, newest last (shared by all windows of this FileStar).</summary>
public static class UndoStack
{
    const int Limit = 50;
    static readonly List<UndoAction> Actions = new();

    public static event Action? Changed;

    public static void Push(UndoAction action)
    {
        Actions.Add(action);
        if (Actions.Count > Limit)
            Actions.RemoveAt(0);
        Changed?.Invoke();
    }

    public static UndoAction? Peek => Actions.Count > 0 ? Actions[^1] : null;

    /// <summary>Undoes the latest operation; returns what was undone (null if nothing) and any problem.</summary>
    public static async Task<(UndoAction? Action, string? Problem)> UndoAsync(IntPtr owner)
    {
        if (Actions.Count == 0)
            return (null, null);
        var action = Actions[^1];
        Actions.RemoveAt(Actions.Count - 1);
        Changed?.Invoke();
        try
        {
            return (action, await action.UndoAsync(owner));
        }
        catch (Exception ex)
        {
            return (action, ex.Message);
        }
    }
}

/// <summary>Puts a recycled item back where it was, the way the Recycle Bin's own 복원 does.</summary>
static class RecycleBin
{
    /// <summary>
    /// Each recycled item is a pair in X:\$Recycle.Bin\{user SID}: "$R…" (the item itself) and "$I…" (its
    /// original path and deletion time). Restoring moves $R back and removes $I.
    /// </summary>
    public static bool Restore(string originalPath, DateTime from, DateTime to)
    {
        try
        {
            string? root = Path.GetPathRoot(originalPath);
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (root == null || sid == null)
                return false;
            string bin = Path.Combine(root, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin))
                return false;
            // The deletion made by that operation (the same path may have been recycled again since).
            var match = Directory.EnumerateFiles(bin, "$I*")
                .Select(info => (Info: info, Record: ReadInfo(info)))
                .Where(x => x.Record is { } r && string.Equals(r.Path, originalPath, StringComparison.OrdinalIgnoreCase) &&
                            r.Deleted >= from.AddSeconds(-2) && r.Deleted <= to.AddSeconds(2))
                .OrderBy(x => x.Record!.Value.Deleted)   // that operation's deletion came before any later one
                .FirstOrDefault();
            if (match.Info == null)
                return false;
            string item = Path.Combine(bin, "$R" + Path.GetFileName(match.Info)[2..]);
            if (File.Exists(originalPath) || Directory.Exists(originalPath))
                return false;   // something new took its place; don't overwrite it
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            if (Directory.Exists(item))
                Directory.Move(item, originalPath);
            else if (File.Exists(item))
                File.Move(item, originalPath);
            else
                return false;
            File.Delete(match.Info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>$I file: version (8 bytes), size (8), deletion time (8), then the original path (version 2: length-prefixed UTF-16).</summary>
    static (string Path, DateTime Deleted)? ReadInfo(string infoFile)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoFile);
            if (bytes.Length < 28)
                return null;
            long version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(bytes, 16));
            string path = version >= 2
                ? Encoding.Unicode.GetString(bytes, 28, Math.Min(BitConverter.ToInt32(bytes, 24) * 2, bytes.Length - 28))
                : Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            return (path.TrimEnd('\0'), deleted);
        }
        catch
        {
            return null;
        }
    }
}
