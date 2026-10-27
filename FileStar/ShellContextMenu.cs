using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace FileStar;

/// <summary>
/// Shows the real Windows Explorer right-click menu (including items added by installed programs, such as
/// compression tools, antivirus scans or OneDrive sharing) for files/folders, or for a folder's empty background.
/// </summary>
public static class ShellContextMenu
{
    /// <summary>Called for verbs that only work inside Explorer itself; return true if handled.</summary>
    public delegate bool VerbHandler(string verb, IReadOnlyList<string> paths);

    const int FirstCommand = 1, LastCommand = 0x7FFF;
    const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120, WM_MENUSELECT = 0x11F;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000, CMIC_MASK_FLAG_NO_UI = 0x400;
    const uint GCS_VERBW = 0x4;

    /// <summary>
    /// A built but not yet shown menu. Building is the slow part (every shell extension adds its items), so it
    /// can be done ahead of time — e.g. while FileStar's own menu is open — and shown instantly later.
    /// </summary>
    public sealed class PreparedMenu : IDisposable
    {
        internal IContextMenu? Menu;
        internal IntPtr HMenu;

        internal PreparedMenu(IContextMenu menu, IntPtr hMenu, IReadOnlyList<string> paths, bool background, bool extended)
        {
            Menu = menu;
            HMenu = hMenu;
            Paths = paths;
            IsBackground = background;
            Extended = extended;
        }

        public IReadOnlyList<string> Paths { get; }
        public bool IsBackground { get; }
        public bool Extended { get; }

        public bool Matches(IReadOnlyList<string> paths, bool background, bool extended) =>
            Menu != null && IsBackground == background && Extended == extended &&
            Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (HMenu != IntPtr.Zero)
                DestroyMenu(HMenu);
            HMenu = IntPtr.Zero;
            if (Menu != null)
                Marshal.FinalReleaseComObject(Menu);
            Menu = null;
        }
    }

    public static PreparedMenu PrepareForItems(Window owner, IReadOnlyList<string> paths, bool extended) =>
        Prepare(CreateForItems(new WindowInteropHelper(owner).Handle, paths), paths, background: false, extended);

    public static PreparedMenu PrepareForFolderBackground(Window owner, string folderPath, bool extended) =>
        Prepare(CreateForFolderBackground(new WindowInteropHelper(owner).Handle, folderPath), new[] { folderPath }, background: true, extended);

    static PreparedMenu Prepare(IContextMenu menu, IReadOnlyList<string> paths, bool background, bool extended)
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0);
            Marshal.ThrowExceptionForHR(menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags));
            return new PreparedMenu(menu, hMenu, paths, background, extended);
        }
        catch
        {
            DestroyMenu(hMenu);
            Marshal.FinalReleaseComObject(menu);
            throw;
        }
    }

    /// <summary>
    /// Runs a shell verb ("copy", "cut", "delete", …) on items without showing a menu — exactly what Explorer's
    /// own command does, so the clipboard format, Recycle Bin and confirmations match Explorer.
    /// </summary>
    /// <param name="shift">As if Shift were held: for "delete" this deletes permanently instead of using the Recycle Bin.</param>
    /// <param name="noUi">Suppress confirmations (only for automated tests).</param>
    public static void InvokeVerb(Window owner, IReadOnlyList<string> paths, string verb, bool shift = false, bool noUi = false)
    {
        if (paths.Count == 0)
            return;
        var hwnd = new WindowInteropHelper(owner).Handle;
        uint extra = (shift ? CMIC_MASK_SHIFT_DOWN : 0) | (noUi ? CMIC_MASK_FLAG_NO_UI : 0);
        InvokeVerb(CreateForItems(hwnd, paths), hwnd, verb, Path.GetDirectoryName(paths[0]), extra);
    }

    /// <summary>Runs a folder-background verb, e.g. "paste" into <paramref name="folderPath"/>.</summary>
    public static void InvokeFolderVerb(Window owner, string folderPath, string verb)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        InvokeVerb(CreateForFolderBackground(hwnd, folderPath), hwnd, verb, folderPath);
    }

    static void InvokeVerb(IContextMenu menu, IntPtr hwnd, string verb, string? directory, uint extraMask = 0)
    {
        IntPtr hMenu = CreatePopupMenu();
        IntPtr verbA = Marshal.StringToHGlobalAnsi(verb), verbW = Marshal.StringToHGlobalUni(verb);
        try
        {
            // Handlers only accept verbs after building their menu.
            Marshal.ThrowExceptionForHR(menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, CMF_NORMAL | CMF_EXPLORE));
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | extraMask,
                hwnd = hwnd,
                lpVerb = verbA,
                lpVerbW = verbW,
                lpDirectoryW = directory,
                nShow = 1,
            };
            menu.InvokeCommand(ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(verbA);
            Marshal.FreeHGlobal(verbW);
            DestroyMenu(hMenu);
            Marshal.FinalReleaseComObject(menu);
        }
    }

    /// <summary>Menu for the given items (all in the same folder).</summary>
    public static void ShowForItems(Window owner, IReadOnlyList<string> paths, Point screenPoint, bool extended, VerbHandler? handleVerb = null)
    {
        if (paths.Count > 0)
            Show(owner, PrepareForItems(owner, paths, extended), screenPoint, handleVerb);
    }

    /// <summary>Menu for a folder's empty background ("새로 만들기", "붙여넣기", …).</summary>
    public static void ShowForFolderBackground(Window owner, string folderPath, Point screenPoint, bool extended) =>
        Show(owner, PrepareForFolderBackground(owner, folderPath, extended), screenPoint, null);

    /// <summary>
    /// Builds (but never shows) the menus once on a background thread. The first menu of a session is slow
    /// (~1.5 s here) because every installed shell extension's DLL loads then; afterwards the DLLs stay
    /// loaded in the process, so the first menu the user opens appears as fast as later ones.
    /// </summary>
    public static void PrewarmInBackground(string? samplePath, string folderPath)
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (samplePath != null)
                    BuildAndDiscard(CreateForItems(IntPtr.Zero, new[] { samplePath }));
                BuildAndDiscard(CreateForFolderBackground(IntPtr.Zero, folderPath));
            }
            catch
            {
                // Best effort: if it fails the real menu is just slower the first time.
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);   // shell extensions are apartment-threaded COM objects
        thread.Start();
    }

    static void BuildAndDiscard(IContextMenu menu)
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME);
        }
        finally
        {
            DestroyMenu(hMenu);
            Marshal.FinalReleaseComObject(menu);
        }
    }

    static IContextMenu CreateForItems(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>();
        object? parentObject = null;
        try
        {
            foreach (var path in paths)
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
                pidls.Add(pidl);
            }
            var shellFolderIid = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0], ref shellFolderIid, out parentObject, out _));
            var parent = (IShellFolder)parentObject;
            // Child IDs are the last segment of each absolute ID (all items share the parent).
            var children = pidls.Select(ILFindLastID).ToArray();
            var menuIid = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref menuIid, IntPtr.Zero, out var menuObject));
            return (IContextMenu)menuObject;
        }
        finally
        {
            if (parentObject != null)
                Marshal.FinalReleaseComObject(parentObject);
            foreach (var pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    static IContextMenu CreateForFolderBackground(IntPtr hwnd, string folderPath)
    {
        IntPtr pidl = IntPtr.Zero;
        object? desktopObject = null, folderObject = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(folderPath, IntPtr.Zero, out pidl, 0, out _));
            Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktopObject));
            var desktop = (IShellFolder)desktopObject;
            var shellFolderIid = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(desktop.BindToObject(pidl, IntPtr.Zero, ref shellFolderIid, out folderObject));
            var menuIid = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(((IShellFolder)folderObject).CreateViewObject(hwnd, ref menuIid, out var menuObject));
            return (IContextMenu)menuObject;
        }
        finally
        {
            if (folderObject != null)
                Marshal.FinalReleaseComObject(folderObject);
            if (desktopObject != null)
                Marshal.FinalReleaseComObject(desktopObject);
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Shows a prepared menu, runs the chosen command, and disposes the menu.</summary>
    public static void Show(Window owner, PreparedMenu prepared, Point screenPoint, VerbHandler? handleVerb)
    {
        var menu = prepared.Menu ?? throw new ObjectDisposedException(nameof(PreparedMenu));
        IntPtr hMenu = prepared.HMenu;
        var paths = prepared.Paths;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        // "보내기", "연결 프로그램" and owner-drawn items fill themselves in through these window messages.
        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR or WM_MENUSELECT)
            {
                if (menu is IContextMenu3 menu3)
                {
                    if (menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                    {
                        handled = msg != WM_MENUSELECT;
                        return result;
                    }
                }
                else if (menu is IContextMenu2 menu2 && menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = msg != WM_MENUSELECT;
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        };
        try
        {
            source.AddHook(hook);
            uint command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            source.RemoveHook(hook);
            if (command < FirstCommand)
                return;

            int offset = (int)command - FirstCommand;
            string verb = GetVerb(menu, offset);
            if (verb.Length > 0 && handleVerb?.Invoke(verb, paths) == true)
                return;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE |
                        (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0) |
                        (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = Path.GetDirectoryName(paths[0]),
                nShow = 1,   // SW_SHOWNORMAL
                ptInvoke = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y },
            };
            menu.InvokeCommand(ref info);
        }
        finally
        {
            source.RemoveHook(hook);
            prepared.Dispose();
        }
    }

    static string GetVerb(IContextMenu menu, int offset)
    {
        var buffer = new StringBuilder(256);
        try
        {
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, buffer.Capacity) == 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Current mouse position in screen pixels (what TrackPopupMenuEx expects).</summary>
    public static Point CursorPosition() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point();

    // ───────────── Interop ─────────────

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint inOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls,
            ref Guid riid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
    }

    // COM interface inheritance: the base methods must be redeclared, in order, for the vtable to line up.
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint flags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr pidlLast);

    [DllImport("shell32.dll")]
    static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out object ppshf);

    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
}
