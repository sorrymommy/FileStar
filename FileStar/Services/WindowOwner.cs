namespace FileStar.Services;

/// <summary>
/// The window that owns Windows' own dialogs (progress, "파일 바꾸기", Recycle Bin confirmations) and FileStar's
/// message boxes. The main window registers itself once it has a handle.
/// </summary>
public interface IWindowOwner
{
    IntPtr Handle { get; }
    System.Windows.Window? Window { get; }
}

public sealed class WindowOwner : IWindowOwner
{
    public System.Windows.Window? Window { get; set; }

    public IntPtr Handle => Window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(Window).Handle;
}
