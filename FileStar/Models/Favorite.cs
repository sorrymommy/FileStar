using System.Collections.ObjectModel;
using System.IO;

namespace FileStar.Models;

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
        new() { Name = name ?? Locations.DisplayName(path), FullPath = path };

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

/// <summary>Lookups in a favorites tree (the store's, or the manager window's working copy).</summary>
public static class FavoriteTree
{
    /// <summary>All groups with a "부모 › 자식" label, depth-first.</summary>
    public static IEnumerable<(Favorite Group, string Label)> Groups(IEnumerable<Favorite> items, string prefix = "")
    {
        foreach (var group in items.Where(f => f.IsGroup))
        {
            string label = prefix + group.Name;
            yield return (group, label);
            foreach (var nested in Groups(group.Children, label + " › "))
                yield return nested;
        }
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

    /// <summary>Removes every item matching <paramref name="match"/>, at any depth; true if any was removed.</summary>
    public static bool RemoveWhere(ObservableCollection<Favorite> list, Func<Favorite, bool> match)
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
}
