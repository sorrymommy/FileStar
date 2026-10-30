using System.IO;

namespace FileStar.Services;

/// <summary>What undoing an operation may need: memos follow files back, and cross-drive moves go through Windows.</summary>
public sealed record UndoContext(INotesStore Notes, IFileOperations Files);

/// <summary>One file operation done in FileStar that Ctrl+Z can take back.</summary>
public abstract record UndoAction(string Description)
{
    /// <summary>Reverses the operation; returns a short message about anything that couldn't be put back.</summary>
    public abstract Task<string?> UndoAsync(UndoContext context);

    protected static string Describe(string what, IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"{what} ({Path.GetFileName(paths[0])})" : $"{what} ({paths.Count:N0}개)";
}

/// <summary>Rename (in the list or the tree): give the old name back.</summary>
public sealed record RenameUndo(string OldPath, string NewPath)
    : UndoAction($"이름 바꾸기 ({Path.GetFileName(NewPath)} → {Path.GetFileName(OldPath)})")
{
    public override Task<string?> UndoAsync(UndoContext context)
    {
        if (!DiskPaths.Exists(NewPath))
            return Task.FromResult<string?>($"'{Path.GetFileName(NewPath)}'이(가) 더 이상 없습니다.");
        DiskPaths.Move(NewPath, OldPath);
        context.Notes.Rename(NewPath, OldPath);
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Move (paste after cut, drag): move everything back to where it came from.</summary>
public sealed record MoveUndo(IReadOnlyList<(string Source, string Target)> Items)
    : UndoAction(Describe("이동", Items.Select(i => i.Target).ToList()))
{
    public override async Task<string?> UndoAsync(UndoContext context)
    {
        int missing = 0;
        foreach (var (source, target) in Items)
        {
            if (!DiskPaths.Exists(target) || DiskPaths.Exists(source))
            {
                missing++;
                continue;
            }
            if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                // Same drive: a plain rename back, instant and exact (also restores a name changed on arrival).
                DiskPaths.Move(target, source);
                context.Notes.Rename(target, source);
            }
            else
            {
                // Across drives Windows copies and deletes; let it do that with its own progress window.
                string folder = Path.GetDirectoryName(source)!;
                await context.Files.TransferAsync(new[] { target }, folder, move: true, record: false);
                string arrived = Path.Combine(folder, Path.GetFileName(target));
                if (!string.Equals(arrived, source, StringComparison.OrdinalIgnoreCase) && DiskPaths.Exists(arrived))
                {
                    DiskPaths.Move(arrived, source);
                    context.Notes.Rename(arrived, source);
                }
            }
        }
        return missing > 0 ? $"{missing:N0}개는 이미 옮겨졌거나 지워져서 되돌리지 못했습니다." : null;
    }
}

/// <summary>Copy, new folder, zip, extract: remove what was created (to the Recycle Bin, so nothing is lost for good).</summary>
public sealed record CreateUndo(IReadOnlyList<string> Created, string What) : UndoAction(Describe(What, Created))
{
    public override async Task<string?> UndoAsync(UndoContext context)
    {
        var existing = Created.Where(DiskPaths.Exists).ToList();
        if (existing.Count > 0)
        {
            await context.Files.DeleteAsync(existing, permanently: false, noConfirm: true, record: false);
            context.Notes.RemoveTrees(existing.Where(p => !DiskPaths.Exists(p)));
        }
        return existing.Count < Created.Count ? "일부는 이미 지워져 있었습니다." : null;
    }
}

/// <summary>
/// Delete to the Recycle Bin: take the items back out of it, to their original places. <paramref name="From"/>/
/// <paramref name="To"/> bracket the deletion, so a later deletion of the same path (e.g. undoing a copy that
/// replaced it) isn't restored by mistake.
/// </summary>
public sealed record DeleteUndo(IReadOnlyList<string> Paths, DateTime From, DateTime To) : UndoAction(Describe("삭제", Paths))
{
    public override Task<string?> UndoAsync(UndoContext context)
    {
        int failed = Paths.Count(path => !RecycleBin.Restore(path, From, To));
        return Task.FromResult(failed > 0 ? $"{failed:N0}개는 휴지통에서 찾지 못해 복원하지 못했습니다." : null);
    }
}

/// <summary>Several operations undone as one (e.g. renaming many files at once).</summary>
public sealed record GroupUndo(IReadOnlyList<UndoAction> Actions, string Label) : UndoAction(Label)
{
    public override async Task<string?> UndoAsync(UndoContext context)
    {
        var problems = new List<string>();
        foreach (var action in Actions.Reverse())
        {
            if (await action.UndoAsync(context) is string problem)
                problems.Add(problem);
        }
        return problems.Count > 0 ? string.Join(" ", problems.Distinct()) : null;
    }
}

/// <summary>The operations Ctrl+Z can take back, newest last.</summary>
public interface IUndoHistory
{
    event Action? Changed;

    UndoAction? Peek { get; }

    void Push(UndoAction action);

    /// <summary>Undoes the latest operation; returns what was undone (null if nothing) and any problem.</summary>
    Task<(UndoAction? Action, string? Problem)> UndoAsync(UndoContext context);
}

public sealed class UndoHistory : IUndoHistory
{
    const int Limit = 50;
    readonly List<UndoAction> _actions = new();

    public event Action? Changed;

    public UndoAction? Peek => _actions.Count > 0 ? _actions[^1] : null;

    public void Push(UndoAction action)
    {
        _actions.Add(action);
        if (_actions.Count > Limit)
            _actions.RemoveAt(0);
        Changed?.Invoke();
    }

    public async Task<(UndoAction? Action, string? Problem)> UndoAsync(UndoContext context)
    {
        if (_actions.Count == 0)
            return (null, null);
        var action = _actions[^1];
        _actions.RemoveAt(_actions.Count - 1);
        Changed?.Invoke();
        try
        {
            return (action, await action.UndoAsync(context));
        }
        catch (Exception ex)
        {
            return (action, ex.Message);
        }
    }
}
