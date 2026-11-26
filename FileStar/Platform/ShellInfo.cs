using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FileStar.Platform.Native;

namespace FileStar.Platform;

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

    static string IconKey(string path, bool isDirectory, bool exact, out bool perFile)
    {
        string ext = isDirectory ? "" : Path.GetExtension(path);
        perFile = exact || (!isDirectory && PerFileExtensions.Contains(ext));
        return perFile ? "file:" + path : isDirectory ? "<folder>" : "ext:" + ext;
    }

    /// <summary>The icon if it is already cached; never asks the shell, so it is safe on the UI thread.</summary>
    public static bool TryGetCachedIcon(string path, bool isDirectory, bool exact, out ImageSource? icon)
    {
        string key = IconKey(path, isDirectory, exact, out _);
        lock (IconCache)
            return IconCache.TryGetValue(key, out icon);
    }

    public static ImageSource? GetIcon(string path, bool isDirectory, bool exact = false)
    {
        string key = IconKey(path, isDirectory, exact, out bool perFile);
        lock (IconCache)
        {
            if (IconCache.TryGetValue(key, out var cached))
                return cached;
        }
        string ext = isDirectory ? "" : Path.GetExtension(path);

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
            return _thisPcIcon = TakePidlIcon(pidl);
        }
    }

    static ImageSource? _linuxIcon;
    static bool _linuxIconLooked;

    /// <summary>The penguin of Explorer's "Linux" folder (WSL), or null without WSL. A local shell lookup, no WSL start-up.</summary>
    public static ImageSource? LinuxIcon
    {
        get
        {
            if (_linuxIconLooked)
                return _linuxIcon;
            _linuxIconLooked = true;
            if (SHParseDisplayName("::{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}", IntPtr.Zero, out var pidl, 0, out _) != 0)
                return null;
            return _linuxIcon = TakePidlIcon(pidl);
        }
    }

    static ImageSource? _networkIcon, _computerIcon;

    /// <summary>The icon of Explorer's 네트워크.</summary>
    public static ImageSource? NetworkIcon => _networkIcon ??= StockIcon(SIID_MYNETWORK);

    /// <summary>A computer on the network. A stock icon: asking the shell about "\\NAME" itself would reach for the network.</summary>
    public static ImageSource? ComputerIcon => _computerIcon ??= StockIcon(SIID_SERVER);

    static ImageSource? StockIcon(uint id)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        return SHGetStockIconInfo(id, SHGSI_ICON | SHGSI_SMALLICON, ref info) == 0 ? TakeIcon(info.hIcon) : null;
    }

    /// <summary>The small icon of a shell item, freeing <paramref name="pidl"/>.</summary>
    static ImageSource? TakePidlIcon(IntPtr pidl)
    {
        try
        {
            var info = new SHFILEINFO();
            return SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_PIDL | SHGFI_ICON | SHGFI_SMALLICON) != IntPtr.Zero
                ? TakeIcon(info.hIcon)
                : null;
        }
        finally
        {
            ILFree(pidl);
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

    /// <summary>The type name if it is already cached; never asks the shell.</summary>
    public static bool TryGetCachedTypeName(string extension, bool isDirectory, out string? name)
    {
        lock (TypeCache)
            return TypeCache.TryGetValue(isDirectory ? "<folder>" : extension, out name);
    }

    public static string GetTypeName(string extension, bool isDirectory)
    {
        if (TryGetCachedTypeName(extension, isDirectory, out var cached))
            return cached!;
        string key = isDirectory ? "<folder>" : extension;

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

    /// <summary>Caches the type names of <paramref name="items"/> (one shell call per extension) on a background thread.</summary>
    public static Task PrefetchTypeNamesAsync(IReadOnlyList<(string Path, bool IsDirectory, bool ExactIcon, string Extension)> items,
        CancellationToken token = default) =>
        RunOnStaThread(() => FetchTypeNames(items, token));

    /// <summary>
    /// Fetches the type name and icon of every item on a background STA thread (the shell's icon handlers
    /// need one), so the list then reads them straight from the cache. Type names come first, then icons in
    /// the order given, so the caller can put the rows on screen first and fill them in top-down.
    /// <paramref name="iconsReady"/> gets how many items, counted from the start, now have their icon cached:
    /// 0 once every type name is cached, then in batches. Cancelling stops the thread between items.
    /// </summary>
    public static Task PrefetchAsync(IReadOnlyList<(string Path, bool IsDirectory, bool ExactIcon, string Extension)> items,
        IProgress<int>? iconsReady = null, CancellationToken token = default) =>
        RunOnStaThread(() =>
        {
            if (!FetchTypeNames(items, token))
                return;
            iconsReady?.Report(0);
            long lastReport = Environment.TickCount64;
            for (int i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested)
                    return;
                var (path, isDirectory, exactIcon, _) = items[i];
                GetIcon(path, isDirectory, exactIcon);
                // Batched so a folder of thousands doesn't flood the UI thread with one post per row.
                if (Environment.TickCount64 - lastReport >= ReportIntervalMs && i + 1 < items.Count)
                {
                    iconsReady?.Report(i + 1);
                    lastReport = Environment.TickCount64;
                }
            }
            iconsReady?.Report(items.Count);
        });

    const int ReportIntervalMs = 50;

    /// <returns>False if cancelled.</returns>
    static bool FetchTypeNames(IReadOnlyList<(string Path, bool IsDirectory, bool ExactIcon, string Extension)> items, CancellationToken token)
    {
        foreach (var (_, isDirectory, _, extension) in items)
        {
            if (token.IsCancellationRequested)
                return false;
            GetTypeName(extension, isDirectory);
        }
        return true;
    }

    static Task RunOnStaThread(Action work)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { Perf.Log("shell prefetch stopped: " + ex.GetType().Name); }   // the caller falls back per row
            finally { done.SetResult(); }
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
