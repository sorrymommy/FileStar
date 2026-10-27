using System.IO;

namespace FileStar.Services;

/// <summary>Finds memos whose file was moved or deleted outside FileStar, and reattaches the ones it can.</summary>
public static class NotesRepair
{
    /// <summary>
    /// Checks every memo (run off the UI thread): fills in missing file IDs, and for memos whose file is gone,
    /// looks the file up by ID. Memos on drives that aren't connected right now (USB, network) are left alone,
    /// so unplugging a drive never makes its memos look deleted.
    /// </summary>
    public static NoteScanResult Scan(List<(string Path, FileNote Note)> notes)
    {
        var lost = new List<LostNote>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var notedPaths = new HashSet<string>(notes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, note) in notes)
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\"))
                continue;   // network paths: too slow to probe and IDs don't apply
            if (!available.TryGetValue(root, out bool ready))
            {
                try { ready = new DriveInfo(root).IsReady; }
                catch { ready = false; }
                available[root] = ready;
            }
            if (!ready)
                continue;

            if (DiskPaths.Exists(path))
            {
                if (note.FileId == null && FileIdentity.Get(path) is string id)
                    ids[path] = id;
                continue;
            }
            string? now = note.FileId == null ? null : FileIdentity.Resolve(note.FileId);
            // The ID now names a different file that already has its own memo: not ours.
            if (now != null && notedPaths.Contains(now))
                now = null;
            lost.Add(new LostNote(path, note, now));
        }
        // Parents first, so relinking a folder carries its children's memos along before they are looked at.
        lost.Sort((a, b) => a.OldPath.Length.CompareTo(b.OldPath.Length));
        return new NoteScanResult(lost, ids);
    }

    /// <summary>Relinks the plain moves (same name, new place); returns how many memos were reattached.</summary>
    public static int AutoRelink(INotesStore notes, NoteScanResult result)
    {
        notes.ApplyIds(result.NewIds);
        int count = 0;
        foreach (var lost in result.Lost.Where(l => l.SameName))
        {
            if (notes.Relink(lost.OldPath, lost.NewPath!))
                count++;
        }
        return count;
    }
}
