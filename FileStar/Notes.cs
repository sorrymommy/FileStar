using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace FileStar;

/// <param name="FileId">NTFS file ID (see <see cref="FileIdentity"/>), so the memo can find its file after an outside move.</param>
public sealed record FileNote(string Text, DateTime Updated, List<string>? Tags = null, string? FileId = null)
{
    /// <summary>The first non-empty line, shown in the file list's 메모 column.</summary>
    [JsonIgnore]
    public string Summary => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    [JsonIgnore]
    public IReadOnlyList<string> TagList => Tags ?? (IReadOnlyList<string>)Array.Empty<string>();
}

/// <summary>
/// User-written notes and tags about individual files and folders, keyed by full path.
/// Stored in a SQLite database (%AppData%\FileStar\notes.db) so that a change writes only that memo, however
/// many there are; all memos are also kept in memory for instant lookups while browsing. Several FileStar
/// windows can share the database (WAL mode); each picks up the others' changes via <see cref="ReloadIfChangedElsewhere"/>.
/// Call from the UI thread only.
/// </summary>
public static class NotesStore
{
    static readonly Dictionary<string, FileNote> Notes = new(StringComparer.OrdinalIgnoreCase);

    // Changes made since the last Commit(): written to the database together, in one transaction.
    static readonly HashSet<string> PendingWrites = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> PendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    static SqliteConnection? _db;
    static long _dataVersion;

    static string DbPath => AppPaths.DataFile("notes.db");
    static string LegacyJsonPath => AppPaths.DataFile("notes.json");

    /// <summary>Set when the database couldn't be opened or written; memos then live only until FileStar closes.</summary>
    public static string? StorageError { get; private set; }

    public static int Count => Notes.Count;

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
            _db.Open();
            Execute("PRAGMA journal_mode=WAL;");     // other FileStar windows can read while one writes
            Execute("PRAGMA busy_timeout=3000;");   // and wait briefly instead of failing if two write at once
            Execute("""
                CREATE TABLE IF NOT EXISTS notes (
                    path    TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    text    TEXT NOT NULL,
                    updated TEXT NOT NULL,
                    tags    TEXT,
                    file_id TEXT
                );
                CREATE INDEX IF NOT EXISTS notes_file_id ON notes(file_id);
                """);
            ImportLegacyJson();
            ReadAll();
        }
        catch (Exception ex)
        {
            StorageError = "메모 저장소(notes.db)를 열 수 없습니다: " + ex.Message;
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>
    /// Earlier versions kept every memo in notes.json. On first start with the database, move them over once
    /// and keep the old file as notes.json.migrated (a backup; nothing reads it again).
    /// </summary>
    static void ImportLegacyJson()
    {
        if (!File.Exists(LegacyJsonPath) || Scalar("SELECT COUNT(*) FROM notes;") > 0)
            return;
        var legacy = JsonSerializer.Deserialize<Dictionary<string, FileNote>>(File.ReadAllText(LegacyJsonPath));
        if (legacy is { Count: > 0 })
        {
            foreach (var (path, note) in legacy)
            {
                Notes[path] = note;
                PendingWrites.Add(path);
            }
            WritePending();
            Notes.Clear();
        }
        File.Move(LegacyJsonPath, LegacyJsonPath + ".migrated", overwrite: true);
    }

    static void ReadAll()
    {
        Notes.Clear();
        using var command = _db!.CreateCommand();
        command.CommandText = "SELECT path, text, updated, tags, file_id FROM notes;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tags = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(3));
            Notes[reader.GetString(0)] = new FileNote(
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                tags is { Count: > 0 } ? tags : null,
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        _dataVersion = Scalar("PRAGMA data_version;");
        _noteFolders = null;
    }

    /// <summary>
    /// Another FileStar window changed memos since we last looked: reload them. Cheap to call often
    /// (a single PRAGMA); returns true if anything was reloaded.
    /// </summary>
    public static bool ReloadIfChangedElsewhere()
    {
        if (_db == null)
            return false;
        try
        {
            // data_version only moves when a *different* connection commits.
            if (Scalar("PRAGMA data_version;") == _dataVersion)
                return false;
            ReadAll();
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static FileNote? Get(string path) => Notes.TryGetValue(path, out var note) ? note : null;

    /// <summary>
    /// The file was changed after its memo was last written or confirmed, so the memo may no longer match it.
    /// Only memo text counts (tags alone aren't a description of the content), and only files (a folder's
    /// time changes whenever anything inside it does).
    /// </summary>
    public static bool IsStale(FileNote? note, DateTime fileWritten, bool isDirectory) =>
        note != null && !isDirectory && note.Text.Length > 0 && fileWritten > note.Updated.AddSeconds(2);

    /// <summary>"메모 확인함": the memo still fits the changed file, so mark it as current without editing it.</summary>
    public static FileNote? Confirm(string path)
    {
        if (Get(path) is not FileNote note)
            return null;
        var confirmed = note with { Updated = DateTime.Now };
        Put(path, confirmed);
        Commit();
        return confirmed;
    }

    /// <summary>Raised after memos or tags change (saved), so markers on folders can be redrawn.</summary>
    public static event Action? Changed;

    static HashSet<string>? _noteFolders;

    /// <summary>True if any file or folder inside <paramref name="folder"/> (at any depth) has a memo or tags.</summary>
    public static bool FolderHasNotes(string folder)
    {
        if (_noteFolders == null)
        {
            // Every ancestor folder of every noted path, built once and dropped whenever notes change.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Notes.Keys)
            {
                for (string? dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir) && set.Add(dir); dir = Path.GetDirectoryName(dir)) { }
            }
            _noteFolders = set;
        }
        return _noteFolders.Contains(folder.TrimEnd('\\')) || _noteFolders.Contains(folder);
    }

    /// <summary>Every tag in use, most used first.</summary>
    public static List<string> AllTags() => Notes.Values
        .SelectMany(n => n.TagList)
        .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => g.Key)
        .ToList();

    /// <summary>Each tag with how many files and folders carry it, alphabetically (the tree's 태그 branch).</summary>
    public static List<(string Tag, int Count)> TagCounts() => Notes.Values
        .SelectMany(n => n.TagList)
        .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => (g.First(), g.Count()))
        .OrderBy(t => t.Item1, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>"#예산, 보고서" → ["예산", "보고서"].</summary>
    public static IEnumerable<string> ParseTags(string input) => input
        .Split(new[] { ',', ';', '#' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length > 0);

    /// <summary>Saves the memo and tags for <paramref name="path"/>; with neither, the note is deleted.</summary>
    public static FileNote? Set(string path, string text, IEnumerable<string> tags)
    {
        text = text.TrimEnd();
        var tagList = tags.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var existing = Get(path);
        if (text.Length == 0 && tagList.Count == 0)
        {
            if (existing != null)
            {
                Drop(path);
                Commit();
            }
            return null;
        }
        if (existing != null && existing.Text == text && existing.TagList.SequenceEqual(tagList))
            return existing;
        var note = new FileNote(text, DateTime.Now, tagList.Count > 0 ? tagList : null, FileIdentity.Get(path) ?? existing?.FileId);
        Put(path, note);
        Commit();
        return note;
    }

    /// <summary>Moves notes when a file or folder is renamed, including notes on everything inside a folder.</summary>
    public static void Rename(string oldPath, string newPath)
    {
        if (Transfer(oldPath, newPath, move: true))
            Commit();
    }

    /// <summary>
    /// After a paste: notes follow moved items to their new paths, and are copied onto copies — for folders,
    /// the notes on everything inside them too. An item that replaced another takes its source's notes.
    /// </summary>
    public static void CarryOver(IEnumerable<(string Source, string Target)> pasted, bool move)
    {
        bool changed = false;
        foreach (var (source, target) in pasted)
            changed |= Transfer(source, target, move);
        if (changed)
            Commit();
    }

    /// <summary>Re-keys (move) or duplicates (copy) the notes on a path and everything under it; true if any.</summary>
    static bool Transfer(string oldPath, string newPath, bool move)
    {
        oldPath = oldPath.TrimEnd('\\');
        newPath = newPath.TrimEnd('\\');
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return false;
        string prefix = oldPath + "\\";
        var keys = Notes.Keys
            .Where(k => string.Equals(k, oldPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
        {
            var note = Notes[key];
            if (move)
                Drop(key);
            string newKey = newPath + key[oldPath.Length..];
            // A copy is a new file with its own ID; a move to another drive also gets a new one.
            Put(newKey, note with { FileId = FileIdentity.Get(newKey) ?? (move ? note.FileId : null) });
        }
        return keys.Count > 0;
    }

    // ── Repair (메모 정리): finding memos whose file moved outside FileStar ──

    /// <summary>A copy of every memo, for checking on a background thread.</summary>
    public static List<(string Path, FileNote Note)> Snapshot() => Notes.Select(p => (p.Key, p.Value)).ToList();

    /// <summary>Stores file IDs found for memos that didn't have one yet (memos from before IDs were kept).</summary>
    public static void ApplyIds(IReadOnlyDictionary<string, string> ids)
    {
        bool changed = false;
        foreach (var (path, id) in ids)
        {
            if (Notes.TryGetValue(path, out var note) && note.FileId != id)
            {
                Put(path, note with { FileId = id });
                changed = true;
            }
        }
        if (changed)
            Commit();
    }

    /// <summary>Moves a memo (and memos inside a folder) to where its file is now. False if it no longer applies.</summary>
    public static bool Relink(string oldPath, string newPath)
    {
        if (!Notes.ContainsKey(oldPath) || Notes.ContainsKey(newPath))
            return false;   // already handled (e.g. moved along with its folder), or the new place has its own memo
        Transfer(oldPath, newPath, move: true);
        Commit();
        return true;
    }

    /// <summary>Forgets the memos on these paths and on everything inside them (copies that were removed).</summary>
    public static void RemoveTrees(IEnumerable<string> paths)
    {
        var keys = paths.SelectMany(p =>
        {
            string root = p.TrimEnd('\\');
            return Notes.Keys.Where(k => string.Equals(k, root, StringComparison.OrdinalIgnoreCase) ||
                                         k.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
        }).ToList();
        Remove(keys);
    }

    public static void Remove(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (var path in paths)
        {
            if (Notes.ContainsKey(path))
            {
                Drop(path);
                changed = true;
            }
        }
        if (changed)
            Commit();
    }

    // ── Persistence ──

    static void Put(string path, FileNote note)
    {
        Notes[path] = note;
        PendingDeletes.Remove(path);
        PendingWrites.Add(path);
    }

    static void Drop(string path)
    {
        Notes.Remove(path);
        PendingWrites.Remove(path);
        PendingDeletes.Add(path);
    }

    /// <summary>Writes the changed memos (only those) and tells the UI.</summary>
    static void Commit()
    {
        _noteFolders = null;
        try
        {
            WritePending();
        }
        catch (Exception ex)
        {
            StorageError = "메모를 저장하지 못했습니다: " + ex.Message;
        }
        Changed?.Invoke();
    }

    static void WritePending()
    {
        if (_db == null || PendingWrites.Count + PendingDeletes.Count == 0)
            return;
        using var transaction = _db.BeginTransaction();
        using (var delete = _db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM notes WHERE path = $path;";
            var path = delete.Parameters.Add("$path", SqliteType.Text);
            foreach (var key in PendingDeletes)
            {
                path.Value = key;
                delete.ExecuteNonQuery();
            }
        }
        using (var upsert = _db.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO notes (path, text, updated, tags, file_id) VALUES ($path, $text, $updated, $tags, $id)
                ON CONFLICT(path) DO UPDATE SET path = excluded.path, text = excluded.text, updated = excluded.updated,
                                                tags = excluded.tags, file_id = excluded.file_id;
                """;
            var path = upsert.Parameters.Add("$path", SqliteType.Text);
            var text = upsert.Parameters.Add("$text", SqliteType.Text);
            var updated = upsert.Parameters.Add("$updated", SqliteType.Text);
            var tags = upsert.Parameters.Add("$tags", SqliteType.Text);
            var id = upsert.Parameters.Add("$id", SqliteType.Text);
            foreach (var key in PendingWrites)
            {
                var note = Notes[key];
                path.Value = key;
                text.Value = note.Text;
                updated.Value = note.Updated.ToString("o");
                tags.Value = note.Tags is { Count: > 0 } ? JsonSerializer.Serialize(note.Tags) : DBNull.Value;
                id.Value = (object?)note.FileId ?? DBNull.Value;
                upsert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        PendingWrites.Clear();
        PendingDeletes.Clear();
        // Our own commit doesn't move data_version for this connection, but read it anyway so a write that
        // raced with another window's doesn't look like "changed elsewhere" forever.
        _dataVersion = Scalar("PRAGMA data_version;");
    }

    static void Execute(string sql)
    {
        using var command = _db!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static long Scalar(string sql)
    {
        using var command = _db!.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
