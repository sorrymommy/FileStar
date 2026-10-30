using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileStar.Services;

/// <summary>The user-managed favorites tree.</summary>
public interface IFavoritesStore
{
    event Action? Changed;

    ObservableCollection<Favorite> Items { get; }

    /// <summary>All favorite folders (not groups), depth-first in display order.</summary>
    IEnumerable<Favorite> Folders { get; }

    /// <summary>All groups with a "부모 › 자식" label, depth-first.</summary>
    IEnumerable<(Favorite Group, string Label)> Groups();

    bool Contains(string path);

    /// <summary>Adds a folder to <paramref name="group"/> (or the top level).</summary>
    void Add(string path, Favorite? group = null);

    Favorite AddGroup(string name, Favorite? parent = null);

    void Remove(string path);

    void Remove(Favorite item);

    /// <summary>Adds the folder if it isn't a favorite yet, else removes it. Not for virtual locations.</summary>
    void Toggle(string path);

    void Replace(IEnumerable<Favorite> items);

    /// <param name="notify">False when only remembering state (group expand/collapse) that nobody needs to redraw for.</param>
    void Save(bool notify = true);
}

/// <summary>Favorites saved to %AppData%\FileStar\favorites.json.</summary>
public sealed class JsonFavoritesStore : IFavoritesStore
{
    // Groups have FullPath == null and a Children list; the older flat format (no groups) still loads.
    sealed record Entry(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullPath,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Expanded,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<Entry>? Children);

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _filePath;

    JsonFavoritesStore(string filePath, ObservableCollection<Favorite> items)
    {
        _filePath = filePath;
        Items = items;
    }

    public event Action? Changed;

    public ObservableCollection<Favorite> Items { get; private set; }

    public IEnumerable<Favorite> Folders => Items.SelectMany(f => f.SelfAndDescendants()).Where(f => !f.IsGroup);

    public IEnumerable<(Favorite Group, string Label)> Groups() => FavoriteTree.Groups(Items);

    /// <summary>Reads the file, or starts with the usual folders (바탕 화면, 문서, 다운로드, 사용자 폴더).</summary>
    public static JsonFavoritesStore Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(filePath)) ?? new();
                return new JsonFavoritesStore(filePath, new(entries.Select(FromEntry)));
            }
        }
        catch { }
        return new JsonFavoritesStore(filePath, DefaultFavorites());
    }

    static Favorite FromEntry(Entry entry)
    {
        if (entry.FullPath != null)
            return Favorite.Folder(entry.FullPath, entry.Name);
        var group = Favorite.Group(entry.Name);
        group.IsExpanded = entry.Expanded ?? true;
        foreach (var child in entry.Children ?? new())
            group.Children.Add(FromEntry(child));
        return group;
    }

    static Entry ToEntry(Favorite fav) => fav.IsGroup
        ? new Entry(fav.Name, null, fav.IsExpanded, fav.Children.Select(ToEntry).ToList())
        : new Entry(fav.Name, fav.FullPath, null, null);

    static ObservableCollection<Favorite> DefaultFavorites()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new ObservableCollection<Favorite>();
        void Add(string path, string name)
        {
            if (Directory.Exists(path))
                list.Add(Favorite.Folder(path, name));
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "바탕 화면");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "문서");
        Add(Path.Combine(home, "Downloads"), "다운로드");
        Add(home, Path.GetFileName(home));
        return list;
    }

    public bool Contains(string path) =>
        Folders.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));

    public void Add(string path, Favorite? group = null)
    {
        if (Contains(path))
            return;
        (group?.Children ?? Items).Add(Favorite.Folder(path));
        if (group != null)
            group.IsExpanded = true;
        Save();
    }

    public Favorite AddGroup(string name, Favorite? parent = null)
    {
        var group = Favorite.Group(name);
        (parent?.Children ?? Items).Add(group);
        if (parent != null)
            parent.IsExpanded = true;
        Save();
        return group;
    }

    public void Remove(string path)
    {
        if (FavoriteTree.RemoveWhere(Items, f => !f.IsGroup && string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            Save();
    }

    public void Remove(Favorite item)
    {
        if (FavoriteTree.RemoveWhere(Items, f => f == item))
            Save();
    }

    public void Toggle(string path)
    {
        if (Locations.IsVirtual(path))
            return;
        if (Contains(path))
            Remove(path);
        else
            Add(path);
    }

    public void Replace(IEnumerable<Favorite> items)
    {
        Items = new(items);
        Save();
    }

    public void Save(bool notify = true)
    {
        try
        {
            DiskPaths.WriteAllTextAtomically(_filePath, JsonSerializer.Serialize(Items.Select(ToEntry).ToList(), WriteOptions));
        }
        catch { }
        if (notify)
            Changed?.Invoke();
    }
}
