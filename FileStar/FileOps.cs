using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FileStar;

/// <summary>
/// Delete / copy / move through Windows' own file-operation engine (SHFileOperation, which uses IFileOperation
/// underneath): Recycle Bin, progress window, "파일 바꾸기/건너뛰기" and "- 복사본" naming all behave like
/// Explorer. Calling it directly is far quicker than invoking the same verbs through a context menu, which
/// first has to load every installed shell extension.
/// </summary>
public static class FileOps
{
    const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;
    const int DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;

    /// <summary>Moves to the Recycle Bin, or deletes permanently. Confirmations follow the user's Windows settings.</summary>
    /// <param name="silent">No dialogs at all (automated tests only).</param>
    /// <param name="noConfirm">Don't ask first (undoing a copy: the user just asked for it).</param>
    /// <param name="record">Add to the undo list (Ctrl+Z restores from the Recycle Bin).</param>
    public static async Task<bool> DeleteAsync(IntPtr owner, IReadOnlyList<string> paths, bool permanently, bool silent = false,
        bool noConfirm = false, bool record = true)
    {
        ushort flags = permanently ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
        // Explorer only asks before recycling if "삭제 확인 대화 상자 표시" is on; permanent deletes always ask.
        if (!permanently && (noConfirm || !RecycleConfirmationEnabled()))
            flags |= FOF_NOCONFIRMATION;
        if (silent)
            flags |= FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
        var started = DateTime.Now;
        bool ok = await RunAsync(owner, FO_DELETE, paths, null, flags);
        // Only what actually went to the Recycle Bin can come back.
        var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (record && !permanently && gone.Count > 0)
            UndoStack.Push(new DeleteUndo(gone, started, DateTime.Now));
        return ok;
    }

    // ── Clipboard (same format as Explorer, so copy/paste works both ways) ──

    public static void SetClipboard(IReadOnlyList<string> paths, bool cut)
    {
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? DROPEFFECT_MOVE : DROPEFFECT_COPY | DROPEFFECT_LINK)));
        Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>
    /// Pastes clipboard files into <paramref name="folder"/>: copy, or move after a cut. Copying into the same
    /// folder names the copies "이름 - 복사본", as Explorer does; elsewhere Windows asks about name conflicts.
    /// Memos and tags go along: moved with moved files, copied onto copies (see <see cref="MapPasted"/>).
    /// </summary>
    public static async Task<bool> PasteAsync(IntPtr owner, string folder, bool silent = false)
    {
        if (!Clipboard.ContainsFileDropList())
            return false;
        var sources = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4 &&
                    (BitConverter.ToInt32(effect.ToArray(), 0) & DROPEFFECT_MOVE) != 0;
        bool ok = await TransferAsync(owner, sources, folder, move, silent);
        if (ok && move)
            Clipboard.Clear();   // the cut files are gone from their old place, as in Explorer
        return ok;
    }

    /// <summary>
    /// Copies or moves files and folders into <paramref name="folder"/> (paste and drag-and-drop), carrying their
    /// memos and tags along. Copies into their own folder are named "이름 - 복사본".
    /// </summary>
    /// <param name="record">Add to the undo list (Ctrl+Z moves things back or removes the copies).</param>
    public static async Task<bool> TransferAsync(IntPtr owner, IReadOnlyList<string> sources, string folder, bool move, bool silent = false,
        bool record = true)
    {
        if (sources.Count == 0)
            return false;
        bool sameFolder = sources.All(s => string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (move && sameFolder)
            return false;   // moving into the folder it is already in changes nothing

        ushort flags = FOF_ALLOWUNDO;
        if (sameFolder)
            flags |= FOF_RENAMEONCOLLISION;
        if (silent)
            flags |= FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;
        var before = Snapshot(sources, folder);
        bool ok = await RunAsync(owner, move ? FO_MOVE : FO_COPY, sources, folder, flags);
        // Even a cancelled run may have done part of the work, so map whatever arrived.
        var map = MapPasted(sources, folder, before, move);
        NotesStore.CarryOver(map, move);
        if (record && map.Count > 0)
            UndoStack.Push(move ? new MoveUndo(map) : new CreateUndo(map.Select(m => m.Target).ToList(), "복사"));
        return ok;
    }

    /// <summary>What the target folder held before the paste, and each source's size/time (to spot overwrites).</summary>
    sealed record PasteSnapshot(HashSet<string> Names, Dictionary<string, (long Length, DateTime Written)> Sources);

    static PasteSnapshot Snapshot(IReadOnlyList<string> sources, string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                names.Add(Path.GetFileName(entry));
        }
        catch { }
        var info = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            try
            {
                if (File.Exists(source))
                {
                    var file = new FileInfo(source);
                    info[source] = (file.Length, file.LastWriteTimeUtc);
                }
            }
            catch { }
        }
        return new PasteSnapshot(names, info);
    }

    /// <summary>
    /// Works out where each pasted item ended up: under its own name, under a name Windows made up to avoid a
    /// clash ("이름 - 복사본", "이름 (2)"), or over an existing item the user chose to replace. Items that were
    /// skipped or not moved are left out.
    /// </summary>
    static List<(string Source, string Target)> MapPasted(IReadOnlyList<string> sources, string folder, PasteSnapshot before, bool move)
    {
        var after = new List<string>();
        try
        {
            after = Directory.EnumerateFileSystemEntries(folder).Select(e => Path.GetFileName(e)!).ToList();
        }
        catch { }
        var created = after.Where(n => !before.Names.Contains(n)).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new List<(string, string)>();
        foreach (var source in sources)
        {
            string name = Path.GetFileName(source.TrimEnd('\\'));
            if (name.Length == 0)
                continue;   // a drive root
            if (move && (File.Exists(source) || Directory.Exists(source)))
                continue;   // still where it was: skipped or cancelled
            string? landed = null;
            if (!before.Names.Contains(name) && after.Contains(name, StringComparer.OrdinalIgnoreCase))
                landed = name;
            else
            {
                // Renamed to avoid the clash: same stem and extension, plus " - 복사본" / " (2)" or similar.
                bool isFile = before.Sources.ContainsKey(source) || Path.HasExtension(name) && !Directory.Exists(Path.Combine(folder, name));
                string stem = isFile ? Path.GetFileNameWithoutExtension(name) : name;
                string ext = isFile ? Path.GetExtension(name) : "";
                landed = created.FirstOrDefault(n => !claimed.Contains(n) &&
                    n.StartsWith(stem + " ", StringComparison.OrdinalIgnoreCase) &&
                    n.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                // Or it replaced the item of the same name (a move that left the source gone, or an identical copy).
                if (landed == null && before.Names.Contains(name))
                {
                    string target = Path.Combine(folder, name);
                    bool replaced = move || Directory.Exists(source) ||
                        before.Sources.TryGetValue(source, out var src) && File.Exists(target) &&
                        new FileInfo(target) is { } t && t.Length == src.Length && t.LastWriteTimeUtc == src.Written;
                    if (replaced)
                        landed = name;
                }
            }
            if (landed == null)
                continue;
            claimed.Add(landed);
            map.Add((source.TrimEnd('\\'), Path.Combine(folder, landed)));
        }
        return map;
    }

    /// <summary>
    /// Runs the operation on its own thread so FileStar stays responsive during long copies; Windows shows its
    /// progress window owned by FileStar. Returns false if it failed or the user cancelled.
    /// </summary>
    static Task<bool> RunAsync(IntPtr owner, uint operation, IReadOnlyList<string> sources, string? target, ushort flags)
    {
        var done = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = operation,
                    pFrom = string.Join("\0", sources) + "\0",   // list ends with a double null
                    pTo = target == null ? null : target + "\0",
                    fFlags = flags,
                };
                int result = SHFileOperation(ref op);
                done.SetResult(result == 0 && !op.fAnyOperationsAborted);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);   // the progress/conflict UI uses COM
        thread.Start();
        return done.Task;
    }

    static bool RecycleConfirmationEnabled()
    {
        try
        {
            SHGetSettings(out var state, SSF_NOCONFIRMRECYCLE);
            return (state & 0x4) == 0;   // fNoConfirmRecycle is the third bit of SHELLFLAGSTATE
        }
        catch
        {
            return true;
        }
    }

    const uint SSF_NOCONFIRMRECYCLE = 0x8000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll")]
    static extern void SHGetSettings(out int lpsfs, uint dwMask);
}
