using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FileStar.Native;

namespace FileStar;

/// <summary>
/// System icons and type names from the Windows shell, cached. Thread-safe: a folder's icons are fetched on a
/// background STA thread (see <see cref="PrefetchAsync"/>) so drawing the list never waits on the shell.
/// </summary>
internal static class ShellInfo
{
    // Extensions whose icon depends on the file itself rather than its type.
    static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr", ".appref-ms",
    };

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> TypeCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path, bool isDirectory, bool exact = false)
    {
        string ext = isDirectory ? "" : Path.GetExtension(path);
        bool perFile = exact || (!isDirectory && PerFileExtensions.Contains(ext));
        string key = perFile ? "file:" + path : isDirectory ? "<folder>" : "ext:" + ext;
        lock (IconCache)
        {
            if (IconCache.TryGetValue(key, out var cached))
                return cached;
        }

        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        uint attributes = 0;
        string query = path;
        if (!perFile)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            query = isDirectory ? "folder" : "file" + ext;
        }

        var info = new SHFILEINFO();
        var image = SHGetFileInfo(query, attributes, ref info, (uint)Marshal.SizeOf(info), flags) != IntPtr.Zero
            ? TakeIcon(info.hIcon)
            : null;

        lock (IconCache)
        {
            if (IconCache.Count > 5000)
                IconCache.Clear();
            IconCache[key] = image;
        }
        return image;
    }

    static ImageSource? _tagIcon;

    /// <summary>A small blue tag (from the icon font) for the tree's 태그 branch.</summary>
    public static ImageSource TagIcon => _tagIcon ??= GlyphImage("", Color.FromRgb(0x2F, 0x6D, 0xB5));

    static ImageSource? _favoritesIcon;

    /// <summary>A gold star, for the tree's 즐겨찾기 branch.</summary>
    public static ImageSource FavoritesIcon => _favoritesIcon ??= GlyphImage("", Color.FromRgb(0xE8, 0xA3, 0x17));

    static ImageSource? _searchIcon;

    /// <summary>A magnifier, for tabs showing search results.</summary>
    public static ImageSource SearchIcon => _searchIcon ??= GlyphImage("", Color.FromRgb(0x2F, 0x6D, 0xB5));

    static ImageSource GlyphImage(string glyph, Color color)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 14, new SolidColorBrush(color), 1.0);
        var drawing = new GeometryDrawing(new SolidColorBrush(color), null, text.BuildGeometry(new Point(1, 1)));
        // Pad to a 16x16 box so it lines up with the folder icons around it.
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(drawing);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    static ImageSource? _thisPcIcon;

    /// <summary>The "내 PC" icon. It is a virtual folder with no path, so it is looked up by its shell ID.</summary>
    public static ImageSource? ThisPcIcon
    {
        get
        {
            if (_thisPcIcon != null)
                return _thisPcIcon;
            if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DRIVES, out var pidl) != 0)
                return null;
            try
            {
                var info = new SHFILEINFO();
                if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_PIDL | SHGFI_ICON | SHGFI_SMALLICON) != IntPtr.Zero)
                    _thisPcIcon = TakeIcon(info.hIcon);
            }
            finally
            {
                ILFree(pidl);
            }
            return _thisPcIcon;
        }
    }

    /// <summary>Converts a shell icon handle to a frozen image and releases the handle.</summary>
    static ImageSource? TakeIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static string GetTypeName(string extension, bool isDirectory)
    {
        string key = isDirectory ? "<folder>" : extension;
        lock (TypeCache)
        {
            if (TypeCache.TryGetValue(key, out var cached))
                return cached;
        }

        var info = new SHFILEINFO();
        SHGetFileInfo(isDirectory ? "folder" : "file" + extension,
            isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf(info), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);

        string name = info.szTypeName;
        if (string.IsNullOrEmpty(name))
            name = isDirectory ? "파일 폴더" : extension.Length == 0 ? "파일" : extension.TrimStart('.').ToUpperInvariant() + " 파일";
        lock (TypeCache)
            TypeCache[key] = name;
        return name;
    }

    /// <summary>
    /// Fetches the icon and type name of every item on a background STA thread (the shell's icon handlers
    /// need one), so the list then reads them straight from the cache.
    /// </summary>
    public static Task PrefetchAsync(IReadOnlyList<FileItem> items)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var item in items)
                {
                    GetIcon(item.FullPath, item.IsDirectory, item.ExactIcon);
                    GetTypeName(item.RawExtension, item.IsDirectory);
                }
            }
            catch { }
            done.SetResult();
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
