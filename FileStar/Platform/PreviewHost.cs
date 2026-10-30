using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FileStar.Platform;

/// <summary>
/// Hosts a Windows shell preview handler — the same component Explorer's preview pane uses — so Excel, Word,
/// PowerPoint, PDF and other registered formats show their real contents without opening the application.
/// </summary>
public sealed class PreviewHost : HwndHost
{
    const string PreviewHandlerKey = @"ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    const uint CLSCTX_INPROC_SERVER = 0x1, CLSCTX_LOCAL_SERVER = 0x4;
    const uint STGM_READ = 0x0, STGM_SHARE_DENY_NONE = 0x40;

    IntPtr _hwnd;
    IPreviewHandler? _handler;
    IStream? _stream;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_handler != null && GetClientRect(_hwnd, out var rect))
        {
            try { _handler.SetRect(ref rect); }
            catch { }
        }
    }

    /// <summary>The registered preview handler for an extension, looked up the way Explorer does.</summary>
    public static Guid? FindHandler(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;
        static string? Read(string subKey)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(subKey + "\\" + PreviewHandlerKey);
            return key?.GetValue(null) as string;
        }

        var candidates = new List<string> { extension };
        using (var userChoice = Registry.CurrentUser.OpenSubKey(
                   $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice"))
        {
            if (userChoice?.GetValue("ProgId") is string choice)
                candidates.Insert(0, choice);
        }
        using (var extKey = Registry.ClassesRoot.OpenSubKey(extension))
        {
            if (extKey?.GetValue(null) is string progId)
                candidates.Add(progId);
            candidates.Add(@"SystemFileAssociations\" + extension);
            if (extKey?.GetValue("PerceivedType") is string perceived)
                candidates.Add(@"SystemFileAssociations\" + perceived);
        }

        foreach (var candidate in candidates)
        {
            if (Read(candidate) is string value && Guid.TryParse(value, out var clsid))
                return clsid;
        }
        return null;
    }

    /// <summary>Loads <paramref name="path"/> with the given handler. Throws if the handler fails.</summary>
    public void Open(string path, Guid clsid)
    {
        Unload();
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("미리보기 창이 아직 준비되지 않았습니다.");

        // Out-of-process (like Explorer) so a misbehaving handler can't take this app down; some only work in-process.
        var iid = typeof(IPreviewHandler).GUID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out object instance);
        if (hr < 0)
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out instance);
        Marshal.ThrowExceptionForHR(hr);
        _handler = (IPreviewHandler)instance;

        try
        {
            switch (instance)
            {
                case IInitializeWithFile withFile:
                    withFile.Initialize(path, STGM_READ);
                    break;
                case IInitializeWithItem withItem:
                    var itemIid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemIid, out object item));
                    withItem.Initialize(item, STGM_READ);
                    break;
                case IInitializeWithStream withStream:
                    Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out _stream));
                    withStream.Initialize(_stream, STGM_READ);
                    break;
                default:
                    throw new NotSupportedException("이 미리보기 처리기는 파일을 받을 수 없습니다.");
            }

            GetClientRect(_hwnd, out var rect);
            _handler.SetWindow(_hwnd, ref rect);
            _handler.DoPreview();
        }
        catch
        {
            Unload();
            throw;
        }
    }

    public void Unload()
    {
        if (_handler != null)
        {
            try { _handler.Unload(); }
            catch { }
            try { Marshal.FinalReleaseComObject(_handler); }
            catch { }
            _handler = null;
        }
        if (_stream != null)
        {
            try { Marshal.FinalReleaseComObject(_stream); }
            catch { }
            _stream = null;
        }
    }

    // ───────────── Interop ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam, lParam;
        public int time, ptX, ptY;
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithItem
    {
        void Initialize([MarshalAs(UnmanagedType.Interface)] object psi, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate,
        IntPtr pstmTemplate, out IStream ppstm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
