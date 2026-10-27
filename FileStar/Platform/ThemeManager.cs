using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileStar.Platform;

/// <summary>
/// Light/dark switching: swaps the color dictionary (Themes/Light.xaml ↔ Themes/Dark.xaml, always the first
/// merged dictionary of App.xaml) and asks Windows to draw every window's title bar in the matching mode.
/// </summary>
public sealed class ThemeManager : IThemeService
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;

    public ThemeManager()
    {
        // Every window, including dialogs opened later, gets the matching title bar as soon as it has a handle.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    public bool IsDark { get; private set; }

    public void Apply(bool dark)
    {
        IsDark = dark;
        var theme = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/FileStar;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
            dictionaries[0] = theme;
        else
            dictionaries.Add(theme);
        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
    }

    void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        // The caption only repaints on a frame change; nudge it so a live switch shows immediately.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
