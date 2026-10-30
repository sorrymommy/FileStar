using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FileStar.Services;

/// <summary>User-written memos and tags about individual files and folders, keyed by full path. UI thread only.</summary>
public interface INotesStore
{
    /// <summary>Raised after memos or tags change (saved), so markers on folders can be redrawn.</summary>
    event Action? Changed;

    /// <summary>Set when the store couldn't be opened or written; memos then live only until FileStar closes.</summary>
    string? StorageError { get; }

    FileNote? Get(string path);

    /// <summary>Saves the memo and tags for <paramref name="path"/>; with neither, the note is deleted.</summary>
    FileNote? Set(string path, string text, IEnumerable<string> tags);

    /// <summary>"메모 확인함": the memo still fits the changed file, so mark it as current without editing it.</summary>
    FileNote? Confirm(string path);

    /// <summary>Moves notes when a file or folder is renamed, including notes on everything inside a folder.</summary>
    void Rename(string oldPath, string newPath);

    /// <summary>
    /// <see cref="Rename"/> for a rename another program made, as the folder watcher reports it. Skipped when the
    /// old path still exists or the memo's file ID says it belongs to another file: the watcher also reports
    /// renames made here, whose memos have already moved (a swap a↔b replayed would swap them back).
    /// </summary>
    void FollowRename(string oldPath, string newPath);

    /// <summary>
    /// After a paste: notes follow moved items to their new paths, and are copied onto copies — for folders,
    /// the notes on everything inside them too.
    /// </summary>
    void CarryOver(IEnumerable<(string Source, string Target)> pasted, bool move);

    /// <summary>True if any file or folder inside <paramref name="folder"/> (at any depth) has a memo or tags.</summary>
    bool FolderHasNotes(string folder);

    /// <summary>Every tag in use, most used first.</summary>
    List<string> AllTags();

    /// <summary>Each tag with how many files and folders carry it, alphabetically (the tree's 태그 branch).</summary>
    List<(string Tag, int Count)> TagCounts();

    /// <summary>A copy of every memo, for work on a background thread.</summary>
    List<(string Path, FileNote Note)> Snapshot();

    /// <summary>Stores file IDs found for memos that didn't have one yet.</summary>
    void ApplyIds(IReadOnlyDictionary<string, string> ids);

    /// <summary>Moves a memo (and memos inside a folder) to where its file is now. False if it no longer applies.</summary>
    bool Relink(string oldPath, string newPath);

    /// <summary>Forgets the memos on these paths and on everything inside them.</summary>
    void RemoveTrees(IEnumerable<string> paths);

    void Remove(IEnumerable<string> paths);

    /// <summary>Another FileStar window changed memos since we last looked: reload them. True if anything was reloaded.</summary>
    bool ReloadIfChangedElsewhere();
}

/// <summary>
/// Memos in a SQLite database (%AppData%\FileStar\notes.db) so that a change writes only that memo, however many
/// there are; all memos are also kept in memory for instant lookups while browsing. Several FileStar windows can
/// share the database (WAL mode); each picks up the others' changes via <see cref="ReloadIfChangedElsewhere"/>.
/// </summary>
public sealed class SqliteNotesStore : INotesStore, IDisposable
{
    readonly Dictionary<string, FileNote> _notes = new(StringComparer.OrdinalIgnoreCase);

    // Changes made since the last Commit(): written to the database together, in one transaction.
    readonly HashSet<string> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    readonly string _dbPath;
    SqliteConnection? _db;
    long _dataVersion;
    HashSet<string>? _noteFolders;

    SqliteNotesStore(string dbPath) => _dbPath = dbPath;

    public event Action? Changed;

    public string? StorageError { get; private set; }


    string LegacyJsonPath => Path.Combine(Path.GetDirectoryName(_dbPath)!, "notes.json");

    /// <summary>Opens (or creates) the database; a store that can't be opened still works, in memory only.</summary>
    public static SqliteNotesStore Open(string dbPath)
    {
        var store = new SqliteNotesStore(dbPath);
        store.Load();
        return store;
    }

    void Load()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
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
    void ImportLegacyJson()
    {
        if (!File.Exists(LegacyJsonPath) || Scalar("SELECT COUNT(*) FROM notes;") > 0)
            return;
        var legacy = JsonSerializer.Deserialize<Dictionary<string, FileNote>>(File.ReadAllText(LegacyJsonPath));
        if (legacy is { Count: > 0 })
        {
            foreach (var (path, note) in legacy)
            {
                _notes[path] = note;
                _pendingWrites.Add(path);
            }
            WritePending();
            _notes.Clear();
        }
        File.Move(LegacyJsonPath, LegacyJsonPath + ".migrated", overwrite: true);
    }

    void ReadAll()
    {
        _notes.Clear();
        using var command = _db!.CreateCommand();
        command.CommandText = "SELECT path, text, updated, tags, file_id FROM notes;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tags = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(3));
            _notes[reader.GetString(0)] = new FileNote(
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                tags is { Count: > 0 } ? tags : null,
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        _dataVersion = Scalar("PRAGMA data_version;");
        _noteFolders = null;
    }

    public bool ReloadIfChangedElsewhere()
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

    public FileNote? Get(string path) => _notes.TryGetValue(path, out var note) ? note : null;

    public FileNote? Confirm(string path)
    {
        if (Get(path) is not FileNote note)
            return null;
        var confirmed = note with { Updated = DateTime.Now };
        Put(path, confirmed);
        Commit();
        return confirmed;
    }

    public bool FolderHasNotes(string folder)
    {
        if (_noteFolders == null)
        {
            // Every ancestor folder of every noted path, built once and dropped whenever notes change.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _notes.Keys)
            {
                for (string? dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir) && set.Add(dir); dir = Path.GetDirectoryName(dir)) { }
            }
            _noteFolders = set;
        }
        return _noteFolders.Contains(folder.TrimEnd('\\')) || _noteFolders.Contains(folder);
    }

    public List<string> AllTags() => _notes.Values
        .SelectMany(n => n.TagList)
        .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => g.Key)
        .ToList();

    public List<(string Tag, int Count)> TagCounts() => _notes.Values
        .SelectMany(n => n.TagList)
        .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => (g.First(), g.Count()))
        .OrderBy(t => t.Item1, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public FileNote? Set(string path, string text, IEnumerable<string> tags)
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

    public void Rename(string oldPath, string newPath)
    {
        if (Transfer(oldPath, newPath, move: true))
            Commit();
    }

    public void FollowRename(string oldPath, string newPath)
    {
        if (DiskPaths.Exists(oldPath))
            return;
        if (_notes.TryGetValue(oldPath.TrimEnd('\\'), out var note) && note.FileId != null && note.FileId != FileIdentity.Get(newPath))
            return;
        Rename(oldPath, newPath);
    }

    public void CarryOver(IEnumerable<(string Source, string Target)> pasted, bool move)
    {
        bool changed = false;
        foreach (var (source, target) in pasted)
            changed |= Transfer(source, target, move);
        if (changed)
            Commit();
    }

    /// <summary>Re-keys (move) or duplicates (copy) the notes on a path and everything under it; true if any.</summary>
    bool Transfer(string oldPath, string newPath, bool move)
    {
        oldPath = oldPath.TrimEnd('\\');
        newPath = newPath.TrimEnd('\\');
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return false;
        var keys = _notes.Keys.Where(k => Locations.IsSameOrUnder(k, oldPath)).ToList();
        foreach (var key in keys)
        {
            var note = _notes[key];
            if (move)
                Drop(key);
            string newKey = newPath + key[oldPath.Length..];
            // A copy is a new file with its own ID; a move to another drive also gets a new one.
            Put(newKey, note with { FileId = FileIdentity.Get(newKey) ?? (move ? note.FileId : null) });
        }
        return keys.Count > 0;
    }

    public List<(string Path, FileNote Note)> Snapshot() => _notes.Select(p => (p.Key, p.Value)).ToList();

    public void ApplyIds(IReadOnlyDictionary<string, string> ids)
    {
        bool changed = false;
        foreach (var (path, id) in ids)
        {
            if (_notes.TryGetValue(path, out var note) && note.FileId != id)
            {
                Put(path, note with { FileId = id });
                changed = true;
            }
        }
        if (changed)
            Commit();
    }

    public bool Relink(string oldPath, string newPath)
    {
        if (!_notes.ContainsKey(oldPath) || _notes.ContainsKey(newPath))
            return false;   // already handled (e.g. moved along with its folder), or the new place has its own memo
        Transfer(oldPath, newPath, move: true);
        Commit();
        return true;
    }

    public void RemoveTrees(IEnumerable<string> paths) =>
        Remove(paths.SelectMany(p => _notes.Keys.Where(k => Locations.IsSameOrUnder(k, p))).ToList());

    public void Remove(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (var path in paths)
        {
            if (_notes.ContainsKey(path))
            {
                Drop(path);
                changed = true;
            }
        }
        if (changed)
            Commit();
    }

    // ── Persistence ──

    void Put(string path, FileNote note)
    {
        _notes[path] = note;
        _pendingDeletes.Remove(path);
        _pendingWrites.Add(path);
    }

    void Drop(string path)
    {
        _notes.Remove(path);
        _pendingWrites.Remove(path);
        _pendingDeletes.Add(path);
    }

    /// <summary>Writes the changed memos (only those) and tells the UI.</summary>
    void Commit()
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

    void WritePending()
    {
        if (_db == null || _pendingWrites.Count + _pendingDeletes.Count == 0)
            return;
        using var transaction = _db.BeginTransaction();
        using (var delete = _db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM notes WHERE path = $path;";
            var path = delete.Parameters.Add("$path", SqliteType.Text);
            foreach (var key in _pendingDeletes)
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
            foreach (var key in _pendingWrites)
            {
                var note = _notes[key];
                path.Value = key;
                text.Value = note.Text;
                updated.Value = note.Updated.ToString("o");
                tags.Value = note.Tags is { Count: > 0 } ? JsonSerializer.Serialize(note.Tags) : DBNull.Value;
                id.Value = (object?)note.FileId ?? DBNull.Value;
                upsert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        _pendingWrites.Clear();
        _pendingDeletes.Clear();
        // Our own commit doesn't move data_version for this connection, but read it anyway so a write that
        // raced with another window's doesn't look like "changed elsewhere" forever.
        _dataVersion = Scalar("PRAGMA data_version;");
    }

    void Execute(string sql)
    {
        using var command = _db!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    long Scalar(string sql)
    {
        using var command = _db!.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
