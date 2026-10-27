using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileStar;

/// <summary>Either a favorite folder (has a path) or a user-named group that holds other favorites and groups.</summary>
public sealed class Favorite : ObservableObject
{
    string _name = "";
    string? _path;
    bool _isExpanded = true, _isSelected;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Null for groups.</summary>
    public string? FullPath
    {
        get => _path;
        set
        {
            if (Set(ref _path, value))
                OnPropertyChanged(nameof(Exists));
        }
    }

    public bool IsGroup { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>UI-only selection state for the manager window.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public ObservableCollection<Favorite> Children { get; } = new();

    public bool Exists => IsGroup || Directory.Exists(_path);

    /// <summary>The store item this was cloned from, so the manager window can find it again.</summary>
    public Favorite? Origin { get; private set; }

    public static Favorite Group(string name) => new() { Name = name, IsGroup = true };

    public static Favorite Folder(string path, string? name = null) =>
        new() { Name = name ?? FileSystem.DisplayName(path), FullPath = path };

    public Favorite Clone()
    {
        var copy = new Favorite { Name = Name, FullPath = FullPath, IsGroup = IsGroup, IsExpanded = IsExpanded, Origin = this };
        foreach (var child in Children)
            copy.Children.Add(child.Clone());
        return copy;
    }

    public IEnumerable<Favorite> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.SelfAndDescendants())
                yield return item;
    }

    public bool IsAncestorOf(Favorite other) => Children.Any(c => c == other || c.IsAncestorOf(other));
}

/// <summary>User-managed favorites tree, saved to %AppData%\FileStar\favorites.json.</summary>
public static class FavoritesStore
{
    // Groups have FullPath == null and a Children list; the older flat format (no groups) still loads.
    sealed record Entry(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullPath,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Expanded,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<Entry>? Children);

    static string FilePath => AppPaths.DataFile("favorites.json");

    public static ObservableCollection<Favorite> Items { get; private set; } = new();

    public static event Action? Changed;

    /// <summary>All favorite folders (not groups), depth-first in display order.</summary>
    public static IEnumerable<Favorite> Folders => Items.SelectMany(f => f.SelfAndDescendants()).Where(f => !f.IsGroup);

    /// <summary>All groups with a "부모 › 자식" label, depth-first.</summary>
    public static IEnumerable<(Favorite Group, string Label)> Groups() => Groups(Items, "");

    static IEnumerable<(Favorite, string)> Groups(IEnumerable<Favorite> items, string prefix)
    {
        foreach (var group in items.Where(f => f.IsGroup))
        {
            string label = prefix + group.Name;
            yield return (group, label);
            foreach (var nested in Groups(group.Children, label + " › "))
                yield return nested;
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
                Items = new(entries.Select(FromEntry));
                return;
            }
        }
        catch { }
        Items = DefaultFavorites();
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

    public static bool Contains(string path) =>
        Folders.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a folder to <paramref name="group"/> (or the top level).</summary>
    public static void Add(string path, Favorite? group = null)
    {
        if (Contains(path))
            return;
        (group?.Children ?? Items).Add(Favorite.Folder(path));
        if (group != null)
            group.IsExpanded = true;
        Save();
    }

    public static Favorite AddGroup(string name, Favorite? parent = null)
    {
        var group = Favorite.Group(name);
        (parent?.Children ?? Items).Add(group);
        if (parent != null)
            parent.IsExpanded = true;
        Save();
        return group;
    }

    public static void Remove(string path)
    {
        if (RemoveWhere(Items, f => !f.IsGroup && string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            Save();
    }

    public static void Remove(Favorite item)
    {
        if (RemoveWhere(Items, f => f == item))
            Save();
    }

    static bool RemoveWhere(ObservableCollection<Favorite> list, Func<Favorite, bool> match)
    {
        bool removed = false;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (match(list[i]))
            {
                list.RemoveAt(i);
                removed = true;
            }
            else if (list[i].IsGroup)
                removed |= RemoveWhere(list[i].Children, match);
        }
        return removed;
    }

    /// <summary>Finds the list that holds <paramref name="item"/> and the group that owns it (null at the top level).</summary>
    public static (ObservableCollection<Favorite> List, Favorite? Owner)? Locate(
        ObservableCollection<Favorite> list, Favorite item, Favorite? owner = null)
    {
        if (list.Contains(item))
            return (list, owner);
        foreach (var group in list.Where(f => f.IsGroup))
        {
            if (Locate(group.Children, item, group) is { } found)
                return found;
        }
        return null;
    }

    public static void Replace(IEnumerable<Favorite> items)
    {
        Items = new(items);
        Save();
    }

    public static void Save(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var entries = Items.Select(ToEntry).ToList();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, options));
        }
        catch { }
        if (notify)
            Changed?.Invoke();
    }
}
